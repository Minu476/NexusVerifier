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
        ProofGoalGraph? goalGraph = null,
        string? obstructionDiagnosis = null)
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

        if (obstructionDiagnosis is not null)
        {
            // The diagnosis gate's output — the model's own analysis of why it's stuck and a
            // proposed different approach. This is the diagnose-then-pivot context that replaces
            // blind retry. Surfaced prominently so the prover engages with it directly.
            mutableSuffix.AppendLine("# 🧠 Obstruction diagnosis (from analysis step — read carefully)");
            mutableSuffix.AppendLine(
                "The previous attempts were stuck. An analysis step diagnosed the obstruction " +
                "and proposed a different approach. Engage with this diagnosis below — if it " +
                "suggests a reformulation or a specific intermediate lemma, pursue THAT, not " +
                "more variations of what already failed.");
            mutableSuffix.AppendLine(obstructionDiagnosis);
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

    /// <summary>
    /// Builds the obstruction-diagnosis request — a forced natural-language analysis step
    /// that runs BEFORE the next Lean-producing turn, after the prover has hit a structural
    /// violation or sustained stall. Mirrors the discipline documented in OAI's reasoning
    /// walkthroughs: every frontier proof was discovered by (1) naming precisely why the first
    /// approach failed, (2) identifying the missing mathematical structure, then (3) pivoting
    /// to a substantively different formulation — never by retrying tactic variations.
    ///
    /// This is NOT a tactic-producing call. It asks for prose: the diagnosis and a different
    /// idea. The output feeds into the next prover prompt as "# Obstruction diagnosis" context,
    /// replacing the bare "don't rename" warning with actual pivoting guidance.
    /// </summary>
    public LlmRequest BuildDiagnosisRequest(
        string problemStatement,
        string currentSketch,
        ProofState state,
        string rejectionReason,
        int failedAttemptCount,
        int maxOutputTokens = 1024)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Problem");
        sb.AppendLine(problemStatement);
        sb.AppendLine();
        sb.AppendLine("# Current Lean sketch (proof is incomplete — contains `sorry`)");
        sb.AppendLine("```lean");
        sb.AppendLine(currentSketch.Trim());
        sb.AppendLine("```");
        sb.AppendLine();
        if (state.PendingGoals.Length > 0)
        {
            sb.AppendLine("# Pending goals (the `sorry`s that remain)");
            for (int i = 0; i < state.PendingGoals.Length && i < 3; i++)
                sb.AppendLine($"{i + 1}. {Truncate(state.PendingGoals[i], 300)}");
            sb.AppendLine();
        }
        sb.AppendLine("# What happened");
        sb.AppendLine(
            $"The prover has made {failedAttemptCount} attempt(s) that failed. " +
            $"Most recent rejection: {rejectionReason}");
        sb.AppendLine();
        sb.AppendLine("# Your task — diagnose, do NOT produce Lean code");
        sb.AppendLine(
            "You are a mathematical advisor. The automated prover is stuck. Do NOT write " +
            "Lean tactics or a proof. Instead, in 3-6 sentences of prose:");
        sb.AppendLine(
            "1. **Obstruction**: State precisely *why* the direct approach is failing. What " +
            "mathematical structure is missing? What makes this goal hard to close by tactic " +
            "search? (e.g. 'the goal requires a witness that doesn't exist in the current " +
            "context', 'the statement is in the wrong form for decidable/finset tactics', " +
            "'this needs a theorem from outside Mathlib's current API', 'the goal is genuinely " +
            "open and no proof is known'.)");
        sb.AppendLine(
            "2. **Different approach**: Propose ONE substantively different mathematical idea " +
            "for how a proof could go — a different intermediate lemma, a reformulation, a " +
            "classical result that applies. If the goal is genuinely open or intractable for " +
            "automated search, say so plainly.");
        sb.AppendLine();
        sb.AppendLine("Be concrete and mathematical. Do not say 'try harder' or 'use simp'.");

        return new LlmRequest
        {
            Messages =
            [
                new LlmMessage("system",
                    "You are a research mathematician advising an automated theorem prover. " +
                    "Your job is to diagnose why a proof attempt is stuck and propose a " +
                    "mathematically substantive alternative. Be honest — if the problem is " +
                    "genuinely open or beyond automated search, say so."),
                new LlmMessage("user", sb.ToString()),
            ],
            MaxOutputTokens = maxOutputTokens,
            Temperature = 0.6,  // slightly higher — we want creative mathematical ideas
            CacheKey = $"diagnose|{state.SketchHash[..16]}",
        };
    }

    /// <summary>
    /// Builds the between-episode reformulation request. When an episode's approach has
    /// structurally failed (stuck for N episodes), this forces the model to do what the OAI
    /// reasoning walkthroughs show is load-bearing: state precisely why the *approach* (not
    /// just the tactics) is stuck, then produce a *genuinely different* proof strategy as a
    /// new initial sketch. The next episode starts from this reformulated sketch, not a blind
    /// fresh attempt at the same style of proof.
    ///
    /// Distinct from <see cref="BuildDiagnosisRequest"/>: that runs *within* an episode to
    /// prevent reward-hacking on turn N+1. This runs *between* episodes to change the overall
    /// approach. The walkthroughs show both levels matter: within-approach obstruction
    /// diagnosis prevents cheating; between-approach pivot is how the actual proofs were found.
    /// </summary>
    public LlmRequest BuildReformulationRequest(
        string problemStatement,
        string bestSketch,
        int bestSorryCount,
        int episodesAttempted,
        int maxOutputTokens = 4096,
        string? priorAttemptSketch = null,
        IReadOnlyList<string>? priorAttemptErrors = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Problem");
        sb.AppendLine(problemStatement);
        sb.AppendLine();
        sb.AppendLine($"# Approach that has failed (after {episodesAttempted} episode(s), still {bestSorryCount} sorry)");
        sb.AppendLine("```lean");
        sb.AppendLine(bestSketch.Trim());
        sb.AppendLine("```");
        sb.AppendLine();

        // Retry with error feedback: the model already produced a different strategy once, but
        // it didn't compile. Show it the exact error rather than asking for a fresh guess blind —
        // this is what the log line at the call site claims happens, and until this parameter was
        // threaded through, that claim was false (both attempts sent an identical prompt).
        if (priorAttemptSketch is not null && priorAttemptErrors is { Count: > 0 })
        {
            sb.AppendLine("# Your previous reformulation attempt did not compile");
            sb.AppendLine("```lean");
            sb.AppendLine(priorAttemptSketch.Trim());
            sb.AppendLine("```");
            sb.AppendLine("Compiler errors:");
            sb.AppendLine("```");
            foreach (var err in priorAttemptErrors) sb.AppendLine(err);
            sb.AppendLine("```");
            sb.AppendLine(
                "Fix these errors while keeping this a genuinely different strategy from the " +
                "originally-failed approach above — do not revert to it. A syntax or type error " +
                "does not mean the underlying strategy was wrong.");
            sb.AppendLine();
        }

        sb.AppendLine("# Your task — pivot to a DIFFERENT proof strategy");
        sb.AppendLine(
            "The proof approach above has been tried with multiple tactic variations and " +
            "cannot close the remaining sorries. The problem is NOT lack of effort — it's " +
            "that this *approach* (this proof structure, these intermediate steps) is " +
            "structurally inadequate. Do the following:");
        sb.AppendLine();
        sb.AppendLine(
            "1. **Diagnose**: In 2-4 sentences, explain *why* this approach is stuck. What " +
            "is the mathematical obstruction? (e.g. 'the proof requires an intermediate lemma " +
            "that doesn't exist in this form', 'the goal needs to be strengthened before " +
            "induction can apply', 'this needs a classical result or a witness construction " +
            "that the current setup doesn't provide', 'the statement as given is genuinely " +
            "open and no automated proof is plausible'.)");
        sb.AppendLine();
        sb.AppendLine(
            "2. **Pivot**: Produce a COMPLETELY DIFFERENT proof strategy as a new Lean 4 " +
            "sketch. This must not be a minor variation of the failed approach — use a " +
            "different proof technique, different intermediate lemmas, or a different " +
            "decomposition of the problem. The declaration signatures MUST be preserved " +
            "exactly (same theorem names, types, binders) — only the proof strategy changes.");
        sb.AppendLine();
        sb.AppendLine(
            "If the problem is genuinely intractable for automated proof search (a famous " +
            "open conjecture with no known proof technique), say so explicitly and produce " +
            "the best partial-progress sketch you can with `sorry` markers where you identify " +
            "the hardest sub-goals.");
        sb.AppendLine();
        sb.AppendLine("Output exactly one ```lean fence with the complete reformulated sketch.");

        return new LlmRequest
        {
            Messages =
            [
                new LlmMessage("system",
                    "You are a research mathematician. An automated theorem prover has exhausted " +
                    "one proof approach. Your job is to identify precisely why it failed and " +
                    "produce a substantively different proof strategy. Do NOT produce a minor " +
                    "variation of the same approach — that has already been tried and failed. " +
                    "If the problem is genuinely open, say so."),
                new LlmMessage("user", sb.ToString()),
            ],
            MaxOutputTokens = maxOutputTokens,
            Temperature = 0.7,  // high — we want a genuinely different idea
            CacheKey = priorAttemptErrors is { Count: > 0 }
                ? $"reformulate|ep{episodesAttempted}|retry"
                : $"reformulate|ep{episodesAttempted}",
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
