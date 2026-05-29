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
    private readonly INeo4jClient _neo4j;
    private readonly ProofStateEncoder _encoder;
    private readonly PromptBuilder _promptBuilder;
    private readonly ILogger<NexusProverSubagent> _log;

    public NexusProverSubagent(
        ILeanOracle lean,
        TieredLlmRouter router,
        ProofFossilizer fossilizer,
        HallucinationGate hallucinationGate,
        ProofCartographer cartographer,
        INeo4jClient neo4j,
        ProofStateEncoder encoder,
        PromptBuilder promptBuilder,
        ILogger<NexusProverSubagent> log)
    {
        _lean = lean;
        _router = router;
        _fossilizer = fossilizer;
        _hallucinationGate = hallucinationGate;
        _cartographer = cartographer;
        _neo4j = neo4j;
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
        var graphReplayHits = 0;
        var fossilRetrievalSamples = 0;
        var fossilSimilarities = new List<float>();
        var bestMissedSim = 0f;  // highest retrieved sim that fell below direct-substitute threshold
        var triedFossils = new HashSet<string>(); // fossil IDs that failed direct-sub this episode
        var triedGraphTactics = new HashSet<string>(); // state:tactic keys that already failed deterministic replay
        var costAccum = 0m; // per-episode cost accumulated from individual LLM responses
        var graphTelemetry = new GraphProposalTelemetry();
        string? structuralViolationWarning = null; // set when rename-hacking is detected; fed into next turn
        var consecutiveViolations = 0;             // consecutive structural gate rejections this episode
        LlmTier? tierCeiling = null;               // locked after first violation to demote away from Tier 3

        for (int turn = 0; turn < ctx.MaxTurns; turn++)
        {
            ct.ThrowIfCancellationRequested();

            if (lastResult.IsFullyProved)
            {
                // Structural validity gate (defense-in-depth): catches any path
                // (Tier 0.5 replay, etc.) that produced a fully-proved sketch
                // without preserving the original theorem names.
                if (!SketchValidator.IsStructurallyValid(ctx.InitialSketch, sketch))
                {
                    _log.LogWarning(
                        "Turn {T}: structural validity gate rejected fully-proved sketch — " +
                        "original theorem names not preserved; treating episode as stuck",
                        turn);
                    break; // fall through to MaxTurnsReached
                }
                _log.LogInformation("Episode {Ep} solved after {Turn} turns",
                    ctx.EpisodeIndex, turn);
                await _cartographer.ObserveAsync(prevState, ctx.ProblemId, TransitionOutcome.Solved, ct);
                return new EpisodeResult(sketch, EpisodeOutcome.Solved, turn,
                    fossilHits, fossilRetrievalSamples, llmCallsByTier, AvgSim(fossilSimilarities), bestMissedSim,
                    FinalSorryCount: 0, GraphReplayHits: graphReplayHits, CostUsd: costAccum)
                {
                    Tier075Telemetry = graphTelemetry,
                };
            }

            // ---- Tier 0.5: graph path replay ----
            // Find a nearby solved landmark and walk the shortest successful TRANSITION
            // path to it, replaying each stored tactic diff without an LLM call.
            // Only active once the graph has real tactic content (Phase 9+).
            bool graphReplaySucceeded = false;
            if (lastResult.SorryCount > 0)
            {
                var stateVec = _encoder.Encode(prevState);
                var solvedNearby = await _neo4j.NearbySolvedLandmarksAsync(stateVec, topK: 5, ct);
                if (solvedNearby.Count > 0)
                {
                    var currentLandmarkId = ProofCartographer.ComputeLandmarkId(prevState, ctx.ProblemId);
                    foreach (var target in solvedNearby)
                    {
                        var path = await _neo4j.ShortestSuccessfulPathAsync(
                            currentLandmarkId, target.Id, ct);
                        if (path is null || path.Count == 0) continue;

                        var replaySketch = sketch;
                        var replayResult = lastResult;
                        bool anyReplayProgress = false;

                        foreach (var tacticDiff in path)
                        {
                            if (string.IsNullOrWhiteSpace(tacticDiff)) continue;
                            var candidate = SubstituteFirstSorry(replaySketch, tacticDiff);
                            var compiled = await _lean.CompileAsync(candidate, ct);
                            if (compiled.Compiled && compiled.SorryCount < replayResult.SorryCount)
                            {
                                replaySketch = candidate;
                                replayResult = compiled;
                                anyReplayProgress = true;
                                graphReplayHits++;
                            }
                            else break; // path diverged from current state
                        }

                        if (anyReplayProgress)
                        {
                            var replayState = await BuildStateAsync(replaySketch, replayResult, ctx);
                            var replayOutcome = ClassifyOutcome(lastResult, replayResult);
                            await _cartographer.RecordTransitionAsync(
                                prevState, replayState, ctx.ProblemId,
                                tacticSequence: ExtractTacticDiff(sketch, replaySketch),
                                replayOutcome, episodeId: ctx.EpisodeId, ct);
                            await _cartographer.ObserveAsync(replayState, ctx.ProblemId, replayOutcome, ct);
                            if (replayResult.SorryCount < bestSorryCount)
                            {
                                await _fossilizer.FossilizeAsync(
                                    prevState,
                                    subgoalText: string.Join(" ; ", prevState.PendingGoals.Take(2)),
                                    tacticBlock: ExtractTacticDiff(sketch, replaySketch),
                                    sorryReduction: bestSorryCount - replayResult.SorryCount,
                                    sourceProblem: ctx.ProblemId, ct);
                                bestSorryCount = replayResult.SorryCount;
                                turnsSinceProgress = 0;
                            }
                            sketch = replaySketch;
                            lastResult = replayResult;
                            prevState = replayState;
                            _log.LogInformation(
                                "Turn {T}: Tier 0.5 graph replay — sorries now {N}",
                                turn, replayResult.SorryCount);
                            graphReplaySucceeded = true;
                            break;
                        }
                    }
                }
            }
            if (graphReplaySucceeded) continue;

            // ---- 0.75) Logic-first graph-native tactic proposal ----
            // Before fossils and before any model call, query the offline GoalShape/APPLIES
            // graph and deterministically test top candidate tactics on the current sorry.
            if (lastResult.SorryCount > 0)
            {
                var proposalVec = _encoder.Encode(prevState);
                var proposals = await _neo4j.ProposeTacticsFromGoalVectorAsync(
                    proposalVec, neighborK: 12, topK: 8, ct)
                    ?? Array.Empty<GraphTacticProposal>();

                var deterministicTried = 0;
                foreach (var proposal in proposals)
                {
                    if (proposal is null || string.IsNullOrWhiteSpace(proposal.TacticId) || string.IsNullOrWhiteSpace(proposal.TacticText))
                    {
                        continue;
                    }

                    var key = $"{prevState.SketchHash}:{proposal.TacticId}";
                    if (!triedGraphTactics.Add(key))
                    {
                        continue;
                    }

                    // Keep deterministic probing cheap: at most two compile checks per turn.
                    deterministicTried++;
                    graphTelemetry.Tier075Attempts++;
                    var candidateSketch = SubstituteFirstSorry(sketch, proposal.TacticText);
                    var candidateResult = await _lean.CompileAsync(candidateSketch, ct);

                    if (candidateResult.Compiled)
                    {
                        graphTelemetry.Tier075CompileSuccesses++;
                        if (candidateResult.SorryCount < lastResult.SorryCount)
                        {
                            graphTelemetry.Tier075SorryReductions += lastResult.SorryCount - candidateResult.SorryCount;
                        }
                    }

                    if (candidateResult.Compiled && candidateResult.SorryCount < lastResult.SorryCount)
                    {
                        var candidateState = await BuildStateAsync(candidateSketch, candidateResult, ctx);
                        var candidateOutcome = ClassifyOutcome(lastResult, candidateResult);

                        await _cartographer.RecordTransitionAsync(
                            prevState, candidateState, ctx.ProblemId,
                            tacticSequence: proposal.TacticText,
                            candidateOutcome,
                            episodeId: ctx.EpisodeId,
                            ct);
                        await _cartographer.ObserveAsync(candidateState, ctx.ProblemId, candidateOutcome, ct);

                        if (candidateResult.SorryCount < bestSorryCount)
                        {
                            await _fossilizer.FossilizeAsync(
                                prevState,
                                subgoalText: string.Join(" ; ", prevState.PendingGoals.Take(2)),
                                tacticBlock: proposal.TacticText,
                                sorryReduction: bestSorryCount - candidateResult.SorryCount,
                                sourceProblem: ctx.ProblemId,
                                ct);
                            bestSorryCount = candidateResult.SorryCount;
                        }

                        sketch = candidateSketch;
                        lastResult = candidateResult;
                        prevState = candidateState;
                        turnsSinceProgress = 0;
                        graphReplayHits++;
                        graphTelemetry.Tier075AttributedWins++;

                        _log.LogInformation(
                            "Turn {T}: Tier 0.75 graph tactic `{Tac}` (rank={Rank:F3}, sim={Sim:F3}, succ={Succ:F2}) -> sorries {N}",
                            turn,
                            proposal.TacticText,
                            proposal.RankScore,
                            proposal.NearestGoalSimilarity,
                            proposal.HistoricalSuccessRate,
                            candidateResult.SorryCount);
                        graphReplaySucceeded = true;
                        break;
                    }

                    if (deterministicTried >= 2)
                    {
                        break;
                    }
                }
            }
            if (graphReplaySucceeded) continue;

            // ---- 1) Try fossil vault ----
            // Returns up to 5 non-blacklisted candidates sorted by similarity.
            // Probe a small candidate window in-turn to avoid burning a full turn on a
            // single bad fossil substitution; all candidates are passed as LLM hints.
            var fossilCandidates = await TryFossilHitAsync(prevState, ctx, triedFossils, ct);
            if (fossilCandidates.Count > 0) fossilRetrievalSamples++;
            var fossilHint = fossilCandidates.Count > 0 ? fossilCandidates[0] : (FossilMatch?)null;
            string updatedSketch;
            LlmTier turnTier;
            string? directSubFossilId = null;

            var directSubCandidates = fossilCandidates
                .Where(m => m.Similarity >= ctx.FossilDirectSubstituteThreshold)
                .Take(3)
                .ToArray();

            if (directSubCandidates.Length > 0)
            {
                string? selectedSketch = null;
                string? selectedFossilId = null;
                float selectedSimilarity = 0f;

                foreach (var candidate in directSubCandidates)
                {
                    var trialSketch = SubstituteFirstSorry(sketch, candidate.Fossil.TacticBlock);
                    var trialResult = await _lean.CompileAsync(trialSketch, ct);
                    if (trialResult.Compiled && trialResult.SorryCount < lastResult.SorryCount)
                    {
                        selectedSketch = trialSketch;
                        selectedFossilId = candidate.Fossil.Id;
                        selectedSimilarity = candidate.Similarity;
                        break;
                    }

                    triedFossils.Add(candidate.Fossil.Id);
                }

                if (selectedSketch is not null && selectedFossilId is not null)
                {
                    directSubFossilId = selectedFossilId;
                    updatedSketch = selectedSketch;
                    turnTier = LlmTier.Tier0_FossilHit;
                    fossilHits++;
                    fossilSimilarities.Add(selectedSimilarity);
                    await _fossilizer.RecordUseAsync(selectedFossilId, ct);
                    _log.LogDebug(
                        "Turn {T}: direct fossil substitution selected from {N} candidates (sim={Sim:F3})",
                        turn,
                        directSubCandidates.Length,
                        selectedSimilarity);
                }
                else
                {
                    // ---- 2) Hallucination scan ----
                    var warnings = await _hallucinationGate.ScanAsync(sketch, ctx.DomainTag, ct);

                    // ---- 3) Cartographer hint ----
                    var cartoHint = await _cartographer.GetDeadEndHintAsync(prevState, ct);

                    // ---- 4) Build prompt, route to tier ----
                    // Pass all candidates (up to 3) as LLM context hints — not just the best one.
                    if (fossilHint is not null)
                    {
                        fossilSimilarities.Add(fossilHint.Similarity);
                        if (fossilHint.Similarity > bestMissedSim) bestMissedSim = fossilHint.Similarity;
                    }

                    var request = _promptBuilder.BuildProverRequest(
                        ctx.ProblemStatement, sketch, prevState,
                        cartoHint, fossilCandidates, warnings,
                        structuralViolationWarning: structuralViolationWarning);
                    structuralViolationWarning = null; // consumed

                    var routerCtx = new RouterContext
                    {
                        EpisodeIndex = ctx.EpisodeIndex,
                        TurnIndex = turn,
                        TurnsSinceLastProgress = turnsSinceProgress,
                        CurrentSorryCount = prevState.SorryCount,
                        TierCeiling = tierCeiling,
                    };
                    var response = await _router.SendAsync(routerCtx, request, ct);
                    turnTier = response.Tier;
                    llmCallsByTier[(int)turnTier]++;
                    costAccum += response.EstimatedCostUsd;

                    updatedSketch = PromptBuilder.ExtractLeanFromResponse(response.Content);
                    if (string.IsNullOrWhiteSpace(updatedSketch))
                    {
                        _log.LogWarning(
                            "Turn {T} [{Tier}]: LLM returned empty Lean code (raw response len={Len})",
                            turn, turnTier, response.Content.Length);
                        turnsSinceProgress++;
                        continue;
                    }
                }
            }
            else
            {
                // ---- 2) Hallucination scan ----
                var warnings = await _hallucinationGate.ScanAsync(sketch, ctx.DomainTag, ct);

                // ---- 3) Cartographer hint ----
                var cartoHint = await _cartographer.GetDeadEndHintAsync(prevState, ct);

                // ---- 4) Build prompt, route to tier ----
                // Pass all candidates (up to 3) as LLM context hints — not just the best one.
                if (fossilHint is not null)
                {
                    fossilSimilarities.Add(fossilHint.Similarity);
                    if (fossilHint.Similarity > bestMissedSim) bestMissedSim = fossilHint.Similarity;
                }

                var request = _promptBuilder.BuildProverRequest(
                    ctx.ProblemStatement, sketch, prevState,
                    cartoHint, fossilCandidates, warnings,
                    structuralViolationWarning: structuralViolationWarning);
                structuralViolationWarning = null; // consumed

                var routerCtx = new RouterContext
                {
                    EpisodeIndex = ctx.EpisodeIndex,
                    TurnIndex = turn,
                    TurnsSinceLastProgress = turnsSinceProgress,
                    CurrentSorryCount = prevState.SorryCount,
                    TierCeiling = tierCeiling,
                };
                var response = await _router.SendAsync(routerCtx, request, ct);
                turnTier = response.Tier;
                llmCallsByTier[(int)turnTier]++;
                costAccum += response.EstimatedCostUsd;

                updatedSketch = PromptBuilder.ExtractLeanFromResponse(response.Content);
                if (string.IsNullOrWhiteSpace(updatedSketch))
                {
                    _log.LogWarning(
                        "Turn {T} [{Tier}]: LLM returned empty Lean code (raw response len={Len})",
                        turn, turnTier, response.Content.Length);
                    turnsSinceProgress++;
                    continue;
                }
            }

            // ---- 5) Compile via Lean ----
            var compileResult = await _lean.CompileAsync(updatedSketch, ct);

            // ---- 5a/5b) Unified structural validity gate ----
            // Catch ALL forms of reward hacking before any state mutation.
            // Previous two-gate design had an edge case: a hack that matches
            // bestSorryCount (tied-best) or reduces sorry count without beating
            // best (non-best improvement) slipped past the partial gate because
            // the condition required SorryCount < bestSorryCount. Even without
            // fossilization the hacked sketch still became the episode baseline,
            // poisoning future LLM prompts and fossil retrieval vectors.
            // Fix: consolidate into one check that runs on every compiled result.
            if (compileResult.Compiled && !SketchValidator.IsStructurallyValid(ctx.InitialSketch, updatedSketch))
            {
                _log.LogWarning(
                    "Turn {T} [{Tier}]: structural validity gate rejected hacked sketch " +
                    "(IsFullyProved={Full}, SorryCount={N} vs best={B}) — original theorem names not preserved",
                    turn, turnTier, compileResult.IsFullyProved, compileResult.SorryCount, bestSorryCount);
                var hackedState = await BuildStateAsync(updatedSketch, compileResult, ctx);
                await _cartographer.RecordTransitionAsync(
                    prevState, hackedState, ctx.ProblemId,
                    tacticSequence: ExtractTacticDiff(sketch, updatedSketch),
                    TransitionOutcome.DeadEnd,
                    episodeId: ctx.EpisodeId, ct);
                await _cartographer.ObserveAsync(hackedState, ctx.ProblemId, TransitionOutcome.DeadEnd, ct);
                structuralViolationWarning =
                    "Constraint: your output must contain each theorem and lemma declaration exactly as given — " +
                    "identical name, binders, and target type. Only the proof term (after ':=' or 'by') may change. " +
                    "The previous attempt modified a declaration and was automatically rejected. " +
                    "Prove the theorem as stated; do not rename, restate, or substitute it.";

                consecutiveViolations++;

                if (consecutiveViolations == 1)
                {
                    // First violation: demote to Tier 2 for the rest of this episode.
                    // deepseek-reasoner is the only model that renames; deepseek-chat does not.
                    tierCeiling = LlmTier.Tier2_DeepSeekFlash;
                    _log.LogWarning(
                        "Turn {T}: tier ceiling locked to Tier2 after first structural violation", turn);
                }
                else
                {
                    // Second+ consecutive violation: demotion didn't help — abort early.
                    _log.LogWarning(
                        "Turn {T}: {N} consecutive structural violations — aborting episode early to stop budget bleed",
                        turn, consecutiveViolations);
                    return new EpisodeResult(
                        sketch,
                        EpisodeOutcome.StructuralGateRejection,
                        turn + 1,
                        fossilHits,
                        fossilRetrievalSamples,
                        llmCallsByTier,
                        AvgSim(fossilSimilarities),
                        bestMissedSim,
                        FinalSorryCount: lastResult.SorryCount,
                        GraphReplayHits: graphReplayHits,
                        CostUsd: costAccum)
                    {
                        Tier075Telemetry = graphTelemetry,
                    };
                }

                turnsSinceProgress++;
                continue; // sketch/lastResult/prevState/bestSorryCount unchanged — no fossilization
            }

            var newState = await BuildStateAsync(updatedSketch, compileResult, ctx);

            // ---- 6) Determine outcome ----
            var outcome = ClassifyOutcome(lastResult, compileResult);

            // If a direct fossil substitution made no progress, blacklist the fossil and revert.
            // This prevents the same fossil being retried every turn in a tight loop.
            if (directSubFossilId is not null &&
                outcome is TransitionOutcome.DeadEnd or TransitionOutcome.Stalled)
            {
                triedFossils.Add(directSubFossilId);
                _log.LogDebug(
                    "Turn {T}: fossil {Id} blacklisted after {Out}, reverting sketch",
                    turn, directSubFossilId, outcome);
                turnsSinceProgress++;
                continue; // sketch/lastResult/prevState stay at pre-substitution values
            }

            // ---- 7) Record transition ----
            await _cartographer.RecordTransitionAsync(
                prevState, newState, ctx.ProblemId,
                tacticSequence: ExtractTacticDiff(sketch, updatedSketch),
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
                consecutiveViolations = 0; // genuine progress — reset demotion state
                tierCeiling = null;        // allow Tier 3 again if it earned it
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
            fossilHits, fossilRetrievalSamples, llmCallsByTier, AvgSim(fossilSimilarities), bestMissedSim,
            FinalSorryCount: lastResult.SorryCount, GraphReplayHits: graphReplayHits, CostUsd: costAccum)
        {
            Tier075Telemetry = graphTelemetry,
        };
    }

    private static float AvgSim(List<float> sims) =>
        sims.Count > 0 ? sims.Average() : 0f;

    private async Task<IReadOnlyList<FossilMatch>> TryFossilHitAsync(
        ProofState state, EpisodeContext ctx, HashSet<string> triedFossils, CancellationToken ct)
    {
        if (state.SorryCount == 0) return Array.Empty<FossilMatch>();
        var matches = await _fossilizer.FindCandidatesAsync(
            state, topK: 5, minSimilarity: ctx.FossilMatchThreshold, ct);
        return matches.Where(m => !triedFossils.Contains(m.Fossil.Id)).ToArray();
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

    // IsStructurallyValid and TheoremNamesIn live in SketchValidator.cs (shared with NexusOrchestrator).
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
    int FossilRetrievalSamples,
    int[] LlmCallsByTier,
    float AvgFossilSimilarity,
    float BestMissedFossilSim,
    int FinalSorryCount,
    int GraphReplayHits,
    decimal CostUsd = 0m)
{
    public GraphProposalTelemetry Tier075Telemetry { get; init; } = new();
}

public enum EpisodeOutcome
{
    Solved,
    MaxTurnsReached,
    StructuralGateRejection,
    NoProgress,
    Error,
}
