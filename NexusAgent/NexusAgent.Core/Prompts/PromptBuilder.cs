using System.Text;
using NexusAgent.Core.Llm;
using NexusAgent.Core.Memory;
using NexusAgent.Core.Models;
using NexusAgent.Core.Planning;
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
        int maxOutputTokens = 2048,
        string? structuralViolationWarning = null,
        ProofGoalGraph? goalGraph = null)
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
            mutableSuffix.AppendLine("# Lean compiler diagnostics from last attempt");
            foreach (var err in state.ErrorMessages)
            {
                mutableSuffix.AppendLine("```text");
                mutableSuffix.AppendLine(err.Trim());
                mutableSuffix.AppendLine("```");
            }
            mutableSuffix.AppendLine();
        }

        if (state.PendingGoals.Length > 0)
        {
            mutableSuffix.AppendLine("# Pending goals");
            for (int i = 0; i < state.PendingGoals.Length && i < 5; i++)
                mutableSuffix.AppendLine($"{i + 1}. {state.PendingGoals[i]}");
            mutableSuffix.AppendLine();

            // Capped to top 3 goals × top 3 failed tactics each, to protect the
            // prefix-cache-friendly stable/mutable split this file is built around —
            // don't let this grow unbounded across a long episode.
            if (goalGraph is not null)
            {
                var historyLines = new List<string>();
                for (int i = 0; i < state.PendingGoals.Length && i < 3; i++)
                {
                    var goalId = goalGraph.GetOrAddGoal(state.PendingGoals[i]);
                    var failed = goalGraph.FailedAttemptsFor(goalId);
                    var siblings = goalGraph.SiblingsOf(goalId);
                    if (failed.Count == 0 && siblings.Count == 0) continue;

                    var line = new StringBuilder($"- goal {i + 1}: ");
                    if (failed.Count > 0)
                    {
                        var tried = failed.Take(3)
                            .Select(f => $"`{Truncate(f.TacticText, 80)}` ({f.Outcome})");
                        line.Append("already tried and did not work: ").Append(string.Join(", ", tried));
                    }
                    if (siblings.Count > 0)
                    {
                        if (failed.Count > 0) line.Append("; ");
                        line.Append($"{siblings.Count} sibling goal(s) from the same tactic application " +
                                    "are also still open");
                    }
                    historyLines.Add(line.ToString());
                }

                if (historyLines.Count > 0)
                {
                    mutableSuffix.AppendLine("# Goal history (this run)");
                    foreach (var l in historyLines) mutableSuffix.AppendLine(l);
                    mutableSuffix.AppendLine();
                }
            }
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

        if (structuralViolationWarning is not null)
        {
            mutableSuffix.AppendLine("# ⛔ STRUCTURAL VIOLATION — previous attempt REJECTED");
            mutableSuffix.AppendLine(structuralViolationWarning);
            mutableSuffix.AppendLine();
        }

        mutableSuffix.AppendLine("# Your task");
        mutableSuffix.AppendLine(
            "Produce an updated version of the sketch. Output exactly one ```lean fence " +
            "containing the entire updated file. No prose outside the fence.");
        mutableSuffix.AppendLine();
        mutableSuffix.AppendLine("**Hard constraint — do not modify the declarations:**");
        mutableSuffix.AppendLine(
            "The theorem and lemma declarations (names, types, and signatures) in the current " +
            "sketch MUST be preserved exactly. Modify only the proof terms — what comes after " +
            "`:= by` or `:=`. Renaming, removing, or substituting any declaration is detected " +
            "automatically and will cause the response to be rejected.");
        mutableSuffix.AppendLine(
            "Even when a goal above is discussed individually (e.g. \"goal 2\"), always return the " +
            "COMPLETE sketch file — never just the tactic for one subgoal in isolation.");

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
