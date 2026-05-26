using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using NexusAgent.Core.Encoding;
using NexusAgent.Core.Llm;
using NexusAgent.Core.Memory;
using NexusAgent.Core.Models;
using NexusAgent.Core.Oracle;
using NexusAgent.Core.Planning;
using NexusAgent.Core.Prompts;
using NexusAgent.Core.Safety;

namespace NexusAgent.Core.Agent;

/// <summary>
/// Runs a single episode (≤ N turns) attempting to refine a Lean proof sketch
/// toward zero sorry. Equivalent in role to one of DeepMind's Ralph loops, but
/// with DAPSA gating: fossil vault → hallucination check → tiered LLM → Lean
/// verification → fossilize on progress → record landmark transition.
/// </summary>
public sealed class NexusProverSubagent
{
    private readonly ILeanOracle _lean;
    private readonly TieredLlmRouter _router;
    private readonly ProofFossilizer _fossilizer;
    private readonly HallucinationGate _hallucinationGate;
    private readonly ProofCartographer _cartographer;
    private readonly ProofStateEncoder _encoder;
    private readonly PromptBuilder _promptBuilder;
    private readonly ILogger<NexusProverSubagent> _log;

    public NexusProverSubagent(
        ILeanOracle lean,
        TieredLlmRouter router,
        ProofFossilizer fossilizer,
        HallucinationGate hallucinationGate,
        ProofCartographer cartographer,
        ProofStateEncoder encoder,
        PromptBuilder promptBuilder,
        ILogger<NexusProverSubagent> log)
    {
        _lean = lean;
        _router = router;
        _fossilizer = fossilizer;
        _hallucinationGate = hallucinationGate;
        _cartographer = cartographer;
        _encoder = encoder;
        _promptBuilder = promptBuilder;
        _log = log;
    }

    public async Task<EpisodeResult> RunEpisodeAsync(
        EpisodeContext ctx, CancellationToken ct)
    {
        var sketch = ctx.InitialSketch;
        var lastResult = await _lean.CompileAsync(sketch, ct);
        var prevState = await BuildStateAsync(sketch, lastResult, ctx);
        var bestSorryCount = lastResult.SorryCount;
        var turnsSinceProgress = 0;
        var llmCallsByTier = new int[4];
        var fossilHits = 0;

        for (int turn = 0; turn < ctx.MaxTurns; turn++)
        {
            ct.ThrowIfCancellationRequested();

            if (lastResult.IsFullyProved)
            {
                _log.LogInformation("Episode {Ep} solved after {Turn} turns",
                    ctx.EpisodeIndex, turn);
                await _cartographer.ObserveAsync(prevState, ctx.ProblemId, TransitionOutcome.Solved, ct);
                return new EpisodeResult(sketch, EpisodeOutcome.Solved, turn,
                    fossilHits, llmCallsByTier);
            }

            // ---- 1) Try fossil vault ----
            var fossilHint = await TryFossilHitAsync(prevState, ctx, ct);
            string updatedSketch;
            LlmTier turnTier;

            if (fossilHint is { } fh && fh.Similarity >= ctx.FossilDirectSubstituteThreshold)
            {
                // Direct substitution attempt
                updatedSketch = SubstituteFirstSorry(sketch, fh.Fossil.TacticBlock);
                turnTier = LlmTier.Tier0_FossilHit;
                fossilHits++;
                _log.LogDebug("Turn {T}: direct fossil substitution (sim={Sim:F3})",
                    turn, fh.Similarity);
            }
            else
            {
                // ---- 2) Hallucination scan ----
                var warnings = await _hallucinationGate.ScanAsync(sketch, ctx.DomainTag, ct);

                // ---- 3) Cartographer hint ----
                var cartoHint = await _cartographer.GetDeadEndHintAsync(prevState, ct);

                // ---- 4) Build prompt, route to tier ----
                var fossilHints = fossilHint is null
                    ? Array.Empty<FossilMatch>()
                    : new[] { fossilHint };

                var request = _promptBuilder.BuildProverRequest(
                    ctx.ProblemStatement, sketch, prevState,
                    cartoHint, fossilHints, warnings);

                var routerCtx = new RouterContext
                {
                    EpisodeIndex = ctx.EpisodeIndex,
                    TurnIndex = turn,
                    TurnsSinceLastProgress = turnsSinceProgress,
                    CurrentSorryCount = prevState.SorryCount,
                };
                var response = await _router.SendAsync(routerCtx, request, ct);
                turnTier = response.Tier;
                llmCallsByTier[(int)turnTier]++;

                updatedSketch = PromptBuilder.ExtractLeanFromResponse(response.Content);
                if (string.IsNullOrWhiteSpace(updatedSketch))
                {
                    _log.LogWarning("Turn {T}: LLM returned empty Lean code", turn);
                    turnsSinceProgress++;
                    continue;
                }
            }

            // ---- 5) Compile via Lean ----
            var compileResult = await _lean.CompileAsync(updatedSketch, ct);
            var newState = await BuildStateAsync(updatedSketch, compileResult, ctx);

            // ---- 6) Determine outcome ----
            var outcome = ClassifyOutcome(lastResult, compileResult);

            // ---- 7) Record transition ----
            await _cartographer.RecordTransitionAsync(
                prevState, newState, ctx.ProblemId,
                tacticSequence: $"tier{(int)turnTier}|turn{turn}",
                outcome,
                episodeId: ctx.EpisodeId,
                ct);
            await _cartographer.ObserveAsync(newState, ctx.ProblemId, outcome, ct);

            // ---- 8) Fossilize on progress ----
            if (compileResult.Compiled && compileResult.SorryCount < bestSorryCount)
            {
                var reduction = bestSorryCount - compileResult.SorryCount;
                await _fossilizer.FossilizeAsync(
                    prevState,
                    subgoalText: string.Join(" ; ", prevState.PendingGoals.Take(2)),
                    tacticBlock: ExtractTacticDiff(sketch, updatedSketch),
                    sorryReduction: reduction,
                    sourceProblem: ctx.ProblemId,
                    ct);
                bestSorryCount = compileResult.SorryCount;
                turnsSinceProgress = 0;
            }
            else
            {
                turnsSinceProgress++;
            }

            sketch = updatedSketch;
            lastResult = compileResult;
            prevState = newState;
        }

        return new EpisodeResult(sketch, EpisodeOutcome.MaxTurnsReached, ctx.MaxTurns,
            fossilHits, llmCallsByTier);
    }

    private async Task<FossilMatch?> TryFossilHitAsync(
        ProofState state, EpisodeContext ctx, CancellationToken ct)
    {
        if (state.SorryCount == 0) return null;
        var matches = await _fossilizer.FindCandidatesAsync(
            state, topK: 3, minSimilarity: ctx.FossilMatchThreshold, ct);
        return matches.Count > 0 ? matches[0] : null;
    }

    private async Task<ProofState> BuildStateAsync(
        string sketch, LeanResult result, EpisodeContext ctx)
    {
        await Task.CompletedTask;
        return new ProofState
        {
            PendingGoals = result.PendingGoalTexts,
            Hypotheses = ExtractHypotheses(sketch),
            TacticHistory = ExtractTactics(sketch),
            SorryCount = result.SorryCount,
            ErrorMessages = result.Errors,
            DomainTag = ctx.DomainTag,
            SketchHash = ComputeHash(sketch),
        };
    }

    private static TransitionOutcome ClassifyOutcome(LeanResult before, LeanResult after)
    {
        if (after.IsFullyProved) return TransitionOutcome.Solved;
        if (!after.Compiled) return TransitionOutcome.DeadEnd;
        if (after.SorryCount < before.SorryCount) return TransitionOutcome.Progressed;
        return TransitionOutcome.Stalled;
    }

    private static string[] ExtractHypotheses(string sketch)
    {
        // Pull `have NAME : TYPE := …` patterns. Production: use Lean's
        // language server for actual hypothesis enumeration.
        var lines = sketch.Split('\n');
        var hyps = new List<string>();
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("have ") || trimmed.StartsWith("let "))
            {
                var colon = trimmed.IndexOf(':');
                if (colon > 0) hyps.Add(trimmed[..colon].Trim());
            }
        }
        return [.. hyps];
    }

    private static string[] ExtractTactics(string sketch)
    {
        var tactics = new List<string>();
        foreach (var line in sketch.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("--")) continue;
            var first = trimmed.Split([' ', '\t'], 2)[0];
            if (first.Length is > 0 and < 20) tactics.Add(first);
        }
        return [.. tactics];
    }

    private static string SubstituteFirstSorry(string sketch, string replacement)
    {
        var idx = sketch.IndexOf("sorry", StringComparison.Ordinal);
        if (idx < 0) return sketch;
        return sketch[..idx] + replacement + sketch[(idx + 5)..];
    }

    private static string ExtractTacticDiff(string oldSketch, string newSketch)
    {
        // Best-effort: return the lines present in newSketch but not in oldSketch.
        var oldLines = new HashSet<string>(oldSketch.Split('\n').Select(l => l.Trim()));
        var added = new List<string>();
        foreach (var line in newSketch.Split('\n'))
        {
            var t = line.Trim();
            if (!string.IsNullOrEmpty(t) && !oldLines.Contains(t)) added.Add(line);
        }
        return string.Join("\n", added);
    }

    private static string ComputeHash(string s)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public sealed record EpisodeContext(
    string ProblemId,
    string ProblemStatement,
    string DomainTag,
    string InitialSketch,
    int EpisodeIndex,
    string EpisodeId,
    int MaxTurns,
    float FossilMatchThreshold,
    float FossilDirectSubstituteThreshold);

public sealed record EpisodeResult(
    string FinalSketch,
    EpisodeOutcome Outcome,
    int TurnsUsed,
    int FossilHits,
    int[] LlmCallsByTier);

public enum EpisodeOutcome
{
    Solved,
    MaxTurnsReached,
    NoProgress,
    Error,
}
