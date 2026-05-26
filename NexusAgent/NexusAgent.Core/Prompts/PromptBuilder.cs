using System.Text;
using NexusAgent.Core.Llm;
using NexusAgent.Core.Memory;
using NexusAgent.Core.Models;
using NexusAgent.Core.Safety;

namespace NexusAgent.Core.Prompts;

/// <summary>
/// Assembles the LLM prompt for a single turn. Designed to maximise DeepSeek
/// V4 prefix-cache hit rate: stable prefix (system prompt, problem statement,
/// imports) precedes mutable suffix (current sketch, errors, hints).
/// </summary>
public sealed class PromptBuilder
{
    public LlmRequest BuildProverRequest(
        string problemStatement,
        string currentSketch,
        ProofState state,
        string? cartographerHint,
        IReadOnlyList<FossilMatch> fossilHints,
        IReadOnlyList<HallucinationWarning> hallucinationWarnings,
        int maxOutputTokens = 2048)
    {
        // STABLE PREFIX — same across all turns of an episode (cache-friendly)
        var stablePrefix = new StringBuilder();
        stablePrefix.AppendLine($"# Problem");
        stablePrefix.AppendLine(problemStatement);
        stablePrefix.AppendLine();
        stablePrefix.AppendLine($"# Domain: {state.DomainTag}");
        stablePrefix.AppendLine();

        // MUTABLE SUFFIX — changes every turn
        var mutableSuffix = new StringBuilder();
        mutableSuffix.AppendLine("# Current sketch");
        mutableSuffix.AppendLine("```lean");
        mutableSuffix.AppendLine(currentSketch.Trim());
        mutableSuffix.AppendLine("```");
        mutableSuffix.AppendLine();

        if (state.ErrorMessages.Length > 0)
        {
            mutableSuffix.AppendLine("# Lean errors from last attempt");
            foreach (var err in state.ErrorMessages.Take(5))
                mutableSuffix.AppendLine($"- {err}");
            mutableSuffix.AppendLine();
        }

        if (state.PendingGoals.Length > 0)
        {
            mutableSuffix.AppendLine("# Pending goals");
            for (int i = 0; i < state.PendingGoals.Length && i < 5; i++)
                mutableSuffix.AppendLine($"{i + 1}. {state.PendingGoals[i]}");
            mutableSuffix.AppendLine();
        }

        if (cartographerHint is not null)
        {
            mutableSuffix.AppendLine("# Navigation hint");
            mutableSuffix.AppendLine(cartographerHint);
            mutableSuffix.AppendLine();
        }

        if (fossilHints.Count > 0)
        {
            mutableSuffix.AppendLine("# Relevant proved sub-goals from prior work");
            mutableSuffix.AppendLine("(These tactic blocks closed similar goals in earlier problems. " +
                                    "Consider adapting them rather than starting from scratch.)");
            foreach (var f in fossilHints.Take(3))
            {
                mutableSuffix.AppendLine($"## Subgoal (similarity={f.Similarity:F2})");
                mutableSuffix.AppendLine($"Statement: {Truncate(f.Fossil.SubgoalText, 200)}");
                mutableSuffix.AppendLine("```lean");
                mutableSuffix.AppendLine(Truncate(f.Fossil.TacticBlock, 800));
                mutableSuffix.AppendLine("```");
                mutableSuffix.AppendLine();
            }
        }

        if (hallucinationWarnings.Count > 0)
        {
            mutableSuffix.AppendLine("# Hallucination warnings");
            foreach (var w in hallucinationWarnings)
                mutableSuffix.AppendLine($"- Lemma `{w.LemmaName}`: {w.Reason} — prove it inline.");
            mutableSuffix.AppendLine();
        }

        mutableSuffix.AppendLine("# Your task");
        mutableSuffix.AppendLine(
            "Produce an updated version of the sketch. Output exactly one ```lean fence " +
            "containing the entire updated file. No prose outside the fence.");

        return new LlmRequest
        {
            Messages =
            [
                new LlmMessage("system", SystemPrompts.ProverSystem),
                new LlmMessage("user",   stablePrefix + "\n" + mutableSuffix),
            ],
            MaxOutputTokens = maxOutputTokens,
            Temperature = 0.4,
            CacheKey = $"prover|{state.SketchHash[..16]}",
        };
    }

    public static string ExtractLeanFromResponse(string content)
    {
        // Find the first ```lean ... ``` fence
        var startMarker = "```lean";
        var startIdx = content.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
        if (startIdx < 0)
        {
            // Some models omit the language tag
            startMarker = "```";
            startIdx = content.IndexOf(startMarker, StringComparison.Ordinal);
            if (startIdx < 0) return content.Trim();
        }
        startIdx += startMarker.Length;
        var endIdx = content.IndexOf("```", startIdx, StringComparison.Ordinal);
        if (endIdx < 0) return content[startIdx..].Trim();
        return content[startIdx..endIdx].Trim();
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
