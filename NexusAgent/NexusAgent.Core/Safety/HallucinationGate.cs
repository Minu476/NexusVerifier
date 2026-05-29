using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NexusAgent.Core.Encoding;
using NexusAgent.Core.Llm;
using NexusAgent.Core.Memory;
using NexusAgent.Core.Models;

namespace NexusAgent.Core.Safety;

/// <summary>
/// Filters helper lemmas marked with `sorry` and described as "known results."
/// This is DeepMind's Failure Mode #2: the LLM cites a fake theorem and the
/// Lean compiler — which can't introspect natural-language comments — accepts
/// the sketch despite the lemma being a fabrication.
///
/// Two-layer defense:
///   1. Fossil-vault check: does any proved fossil resemble this lemma statement?
///      If yes, allow with high confidence.
///   2. LLM screening via Tier 1 (Qwen local, free): ask "is this a real Mathlib
///      theorem name?" — classification-only, no proof generation.
/// </summary>
public sealed partial class HallucinationGate
{
    private readonly ProofFossilizer _fossilizer;
    private readonly ProofStateEncoder _encoder;
    private readonly IReadOnlyList<ILlmClient> _jurors;
    private readonly ILogger<HallucinationGate> _log;
    private readonly HallucinationConfig _cfg;

    public HallucinationGate(
        ProofFossilizer fossilizer,
        ProofStateEncoder encoder,
        IEnumerable<ILlmClient> llmClients,
        ILogger<HallucinationGate> log,
        HallucinationConfig? config = null)
    {
        _fossilizer = fossilizer;
        _encoder = encoder;
        // Jurors: Tier1_Cheap (always present) + any Tier0_GateJuror clients (Gemini, etc.)
        // Majority vote — SUSPECT only if >50% of jurors agree; absent jurors abstain (not SUSPECT).
        _jurors = llmClients
            .Where(c => c.Tier is LlmTier.Tier1_Cheap or LlmTier.Tier0_GateJuror)
            .ToList();
        _log = log;
        _cfg = config ?? new HallucinationConfig();
    }

    /// <summary>
    /// Scan a Lean sketch for sorry-marked helper lemmas and classify each one.
    /// Returns a list of warnings for any suspected hallucinations.
    /// </summary>
    public async Task<IReadOnlyList<HallucinationWarning>> ScanAsync(
        string sketch,
        string domainTag,
        CancellationToken ct)
    {
        var lemmas = ExtractSorryLemmas(sketch);
        if (lemmas.Count == 0) return [];

        var warnings = new List<HallucinationWarning>();
        foreach (var lemma in lemmas)
        {
            var verdict = await ClassifyAsync(lemma, domainTag, ct);
            if (verdict.IsSuspect)
                warnings.Add(new HallucinationWarning(lemma.Name, lemma.Statement, verdict.Reason));
        }
        return warnings;
    }

    private async Task<(bool IsSuspect, string Reason)> ClassifyAsync(
        SorryLemma lemma, string domainTag, CancellationToken ct)
    {
        // Layer 1: fossil-vault check
        var probeState = ProofState.Empty(domainTag) with
        {
            PendingGoals = [lemma.Statement],
            SketchHash = $"hallucination-probe-{lemma.Name}",
        };
        var fossilMatches = await _fossilizer.FindCandidatesAsync(
            probeState, topK: 3, minSimilarity: _cfg.FossilCorroborationThreshold, ct);

        if (fossilMatches.Count > 0)
        {
            _log.LogDebug("Lemma {Name} corroborated by fossil (sim={Sim:F3})",
                lemma.Name, fossilMatches[0].Similarity);
            return (false, "");
        }

        // Layer 2: majority-vote across all gate jurors (Tier1_Cheap + Tier0_GateJuror).
        // Jurors are queried in parallel; a failing juror abstains (counts as REAL).
        // SUSPECT verdict requires a strict majority (> 50%) — one noisy model cannot
        // block a valid lemma. With 3 jurors this means ≥2 must agree to convict.
        var prompt =
            $$"""
            You are a Lean 4 Mathlib expert. Classify the following lemma statement.

            Lemma name claimed by author: {{lemma.Name}}
            Lemma statement:
            {{lemma.Statement}}

            Is this a real, named Mathlib theorem (or a trivially provable elementary fact)?
            Answer with exactly one word: REAL or SUSPECT.
            """;

        var classifyRequest = new LlmRequest
        {
            Messages = [new LlmMessage("user", prompt)],
            MaxOutputTokens = 8,
            Temperature = 0.0,
        };

        var votes = await Task.WhenAll(_jurors.Select(async juror =>
        {
            try
            {
                var response = await juror.CompleteAsync(classifyRequest, ct);
                var verdict = response.Content.Trim().ToUpperInvariant();
                var isSuspect = verdict.StartsWith("SUSPECT");
                _log.LogDebug("Gate juror {Tier} voted {Verdict} for '{Name}'",
                    juror.Tier, isSuspect ? "SUSPECT" : "REAL", lemma.Name);
                return isSuspect;
            }
            catch (Exception ex)
            {
                // Failing juror abstains — does not contribute a SUSPECT vote.
                _log.LogWarning(ex, "Gate juror {Tier} failed for '{Name}' — abstaining",
                    juror.Tier, lemma.Name);
                return false;
            }
        }));

        var suspectCount = votes.Count(v => v);
        var isMajority = suspectCount > votes.Length / 2.0;

        if (isMajority)
        {
            return (true,
                $"Gate majority ({suspectCount}/{votes.Length}) flagged '{lemma.Name}' as not a recognized Mathlib theorem.");
        }

        return (false, "");
    }

    private static List<SorryLemma> ExtractSorryLemmas(string sketch)
    {
        // Matches: `lemma|theorem NAME : STATEMENT := by ... sorry`
        // (Simplified — production grammar should use a proper Lean parser.)
        var result = new List<SorryLemma>();
        foreach (Match m in LemmaPattern().Matches(sketch))
        {
            var name = m.Groups["name"].Value;
            var statement = m.Groups["statement"].Value;
            var body = m.Groups["body"].Value;
            if (body.Contains("sorry"))
                result.Add(new SorryLemma(name, statement));
        }
        return result;
    }

    [GeneratedRegex(
        @"(?:lemma|theorem)\s+(?<name>[\w']+)\s*(?::\s*(?<statement>[^:]+?))\s*:=\s*by(?<body>[^\n]*(?:\n(?!\s*(?:lemma|theorem|end)\b)[^\n]*)*)",
        RegexOptions.Multiline)]
    private static partial Regex LemmaPattern();
}

internal sealed record SorryLemma(string Name, string Statement);

public sealed record HallucinationWarning(string LemmaName, string Statement, string Reason);

public sealed record HallucinationConfig
{
    public float FossilCorroborationThreshold { get; init; } = 0.78f;
}
