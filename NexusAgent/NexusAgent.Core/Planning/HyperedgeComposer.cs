using Microsoft.Extensions.Logging;
using NexusAgent.Core.Memory;

namespace NexusAgent.Core.Planning;

/// <summary>
/// AND-join composer: resolves an open Lean proof goal by recursively matching
/// stored <see cref="HyperedgeRecord"/>s against the goal and its sub-goals.
///
/// <para>Algorithm (bounded depth-first search):</para>
/// <list type="number">
///   <item>Extract the bare conclusion from the Lean compiler's pending-goal text
///       (stripping hypotheses and the <c>⊢</c> prefix).</item>
///   <item>Look up every stored edge whose <c>Output</c> matches that conclusion.</item>
///   <item>For each candidate edge with premises, recursively derive every premise
///       up to <c>maxDepth</c> levels deep.</item>
///   <item>Return the first complete <see cref="DerivationNode"/> tree found,
///       or <c>null</c> when the store cannot close the goal.</item>
/// </list>
///
/// <para>The resulting derivation is converted to a Lean 4 term-mode tactic via
/// <see cref="BuildTacticSketch"/>. The caller validates by round-tripping
/// through <c>ILeanOracle.CompileAsync</c> before accepting the sketch.</para>
///
/// <para>Integration: wired into <see cref="BestFirstGraphPlanner"/> as Tier 0 —
/// checked on every node expansion before any LLM or graph-vector call.</para>
/// </summary>
public sealed class HyperedgeComposer
{
    private readonly INeo4jClient _neo4j;
    private readonly ILogger<HyperedgeComposer> _log;

    public HyperedgeComposer(INeo4jClient neo4j, ILogger<HyperedgeComposer> log)
    {
        _neo4j = neo4j;
        _log   = log;
    }

    /// <summary>
    /// Try to build a complete derivation for <paramref name="pendingGoalText"/>
    /// (as emitted by the Lean compiler's "unsolved goals" message) using stored
    /// hyperedges, bounded to <paramref name="maxDepth"/> levels of AND-join nesting.
    /// </summary>
    /// <returns>
    /// A <see cref="DerivationNode"/> tree whose root closes the goal, or
    /// <c>null</c> if the store cannot supply a complete derivation.
    /// </returns>
    public async Task<DerivationNode?> TryComposeAsync(
        string pendingGoalText,
        int maxDepth,
        CancellationToken ct)
    {
        var conclusion = ExtractConclusion(pendingGoalText);
        if (string.IsNullOrWhiteSpace(conclusion)) return null;

        _log.LogDebug("[HyperedgeComposer] Attempting derivation for: {Goal}", conclusion);
        var result = await ResolveAsync(conclusion, maxDepth, new HashSet<string>(StringComparer.Ordinal), ct);

        if (result is not null)
            _log.LogInformation("[HyperedgeComposer] Derivation found (depth {D}): {Tactic}",
                result.Depth, BuildTacticSketch(result));

        return result;
    }

    /// <summary>
    /// Convert a <see cref="DerivationNode"/> tree into a Lean 4 tactic string
    /// suitable for direct substitution of a <c>sorry</c> placeholder.
    ///
    /// <para>Generated form: <c>exact {term-mode application}</c></para>
    /// <list type="bullet">
    ///   <item>Leaf: <c>exact Nat.add_comm</c></item>
    ///   <item>1-premise: <c>exact dvd_trans dvd_refl lemma2</c></item>
    ///   <item>2-premise depth-2:
    ///       <c>exact dvd_trans (dvd_trans dvd_refl dvd_refl) dvd_refl</c></item>
    /// </list>
    /// </summary>
    public static string BuildTacticSketch(DerivationNode root)
        => $"exact {BuildTerm(root)}";

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<DerivationNode?> ResolveAsync(
        string goalText,
        int remainingDepth,
        HashSet<string> visitedGoals,
        CancellationToken ct)
    {
        if (remainingDepth < 0) return null;

        // Cycle guard: don't try to prove a goal that is already in the recursion stack.
        if (!visitedGoals.Add(goalText)) return null;

        IReadOnlyList<HyperedgeRecord> candidates;
        try
        {
            candidates = await _neo4j.GetHyperedgesByOutputAsync(goalText, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[HyperedgeComposer] Store query failed for: {Goal}", goalText);
            visitedGoals.Remove(goalText);
            return null;
        }

        // Prefer leaf edges (no premises) — they close the goal without recursion.
        foreach (var edge in candidates.OrderBy(e => e.Inputs.Length))
        {
            if (edge.Inputs.Length == 0)
            {
                // Direct proof — leaf of the derivation tree.
                visitedGoals.Remove(goalText);
                return new DerivationNode { Edge = edge, PremiseDerivations = [] };
            }

            if (remainingDepth == 0) continue; // depth budget exhausted for non-leaf

            // AND-join: every premise must be resolvable within the remaining depth.
            var premiseDerivations = new DerivationNode[edge.Inputs.Length];
            var allResolved = true;

            for (var i = 0; i < edge.Inputs.Length; i++)
            {
                var premiseDerivation = await ResolveAsync(
                    edge.Inputs[i], remainingDepth - 1, visitedGoals, ct);

                if (premiseDerivation is null)
                {
                    allResolved = false;
                    break;
                }

                premiseDerivations[i] = premiseDerivation;
            }

            if (allResolved)
            {
                visitedGoals.Remove(goalText);
                return new DerivationNode { Edge = edge, PremiseDerivations = premiseDerivations };
            }
        }

        visitedGoals.Remove(goalText);
        return null;
    }

    /// <summary>
    /// Recursively build a Lean 4 term-mode application string.
    /// Multi-argument sub-terms are wrapped in parentheses to preserve grouping.
    /// </summary>
    private static string BuildTerm(DerivationNode node)
    {
        if (node.IsLeaf) return node.Edge.LemmaName;

        var args = node.PremiseDerivations.Select(premise =>
        {
            var inner = BuildTerm(premise);
            // Compound applications must be parenthesised when used as arguments.
            return inner.Contains(' ') ? $"({inner})" : inner;
        });

        return $"{node.Edge.LemmaName} {string.Join(" ", args)}";
    }

    /// <summary>
    /// Extract the bare conclusion from a Lean compiler pending-goal string.
    ///
    /// <para>Lean's "unsolved goals" output looks like:</para>
    /// <code>
    /// a b : ℕ
    /// hab : a ∣ b
    /// ⊢ a ∣ c
    /// </code>
    /// <para>This method returns <c>a ∣ c</c> — stripping hypotheses and the
    /// <c>⊢</c> prefix — to match <see cref="HyperedgeRecord.Output"/>.</para>
    /// </summary>
    internal static string ExtractConclusion(string pendingGoalText)
    {
        if (string.IsNullOrWhiteSpace(pendingGoalText))
            return string.Empty;

        // Walk lines in reverse to find the last ⊢ line (the conclusion).
        var lines = pendingGoalText.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("⊢ ", StringComparison.Ordinal))
                return line[2..].Trim();
            if (line.StartsWith("⊢", StringComparison.Ordinal))
                return line[1..].Trim();
        }

        // Fallback: no ⊢ found — treat the whole text as the goal.
        return pendingGoalText.Trim();
    }
}
