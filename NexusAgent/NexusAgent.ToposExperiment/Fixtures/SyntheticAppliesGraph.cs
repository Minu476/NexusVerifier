using NexusAgent.ToposExperiment.Models;

namespace NexusAgent.ToposExperiment.Fixtures;

/// <summary>
/// A small synthetic APPLIES-style graph with deliberately-real junctions — the structural
/// property FC100 lacked (branching factor 1.0051, 0.3% junctions per
/// <c>docs/RLB_V2_FUTURE_WORK.md</c> §1). This is the test data the prior experiment couldn't
/// provide; it lets the Topos integration be exercised end-to-end with zero external dependencies
/// (no Neo4j, no LeanDojo corpus), and it's structured so the AND-OR search has real decisions to
/// make rather than degenerating to a linear trace.
///
/// <b>The graph (goal hashes are synthetic but valid 64-hex-shaped strings for OF-1 parity):</b>
///
    ///   theorems T1 and T2 share an intermediate goal G_shared (a real junction — two distinct
    ///   theorem-proofs pass through the same proof state). Each theorem has its own root goal and
    ///   its own solved/leaf goal. T1's root has a genuine OR-choice: two different tactics reach
    ///   different intermediate states, one of which (G_shared) is shared with T2. T2's proof
    ///   includes a tactic that produces TWO subgoals simultaneously (a real AND-branch — the
    ///   n-ary structure that motivated Topos in the first place).
    ///
    ///   T1_root ──tactic_a──→ G_shared ──tactic_c──→ T1_solved (leaf)
    ///           ──tactic_b──→ G_t1_only ──tactic_d──→ T1_solved (leaf)
    ///   T2_root ──tactic_e──→ [G_shared, G_t2_sub]  (AND-branch: tactic produces two subgoals)
    ///           G_shared     ──tactic_g──→ T2_solved_alt (leaf)   [junction reuse, distinct tactic]
    ///           G_t2_sub     ──tactic_f──→ T2_solved (leaf)
    ///
    /// <b>Note on the junction tactic names:</b> T1 closes G_shared via tactic_c, T2 closes the
    /// same shared goal via tactic_g. The grouping rule keys on (GoalBeforeHash, TacticId); if
    /// both theorems used the *same* tactic name on G_shared, the adapter would collapse them
    /// into one hyperedge with two Conditions (one shared edge-vertex, not two). Using distinct
    /// tactic names keeps them as two separate hyperedges both anchored on G_shared — a genuine
    /// two-candidate junction for the chainer's OR-branching. (The collapse case is itself an
    /// interesting semantic question — recorded in the integration report.)
///
/// Hand-computed expectations live in <c>SyntheticAppliesGraphTests</c>.
///
/// <b>Why this is meaningful as a structural test:</b> FC100's NO-GO was a property of the data
/// (no branching, no junctions), not of the learner or harness. This fixture provides exactly the
/// shape that was missing — so a backend that stores and traverses this graph correctly is doing
/// the structural work, independent of whether any real corpus exhibits it.
/// </summary>
public static class SyntheticAppliesGraph
{
    // Short, readable pseudo-hashes padded to 64 hex chars. OF-1 rule says hashes are opaque
    // tokens used verbatim — the content doesn't matter, only that the same string means the same
    // goal across edges. These are obviously synthetic (the prefix makes that clear).
    private const string T1_root      = "synthetic_t1_root________________________________________________";
    private const string T1_solved    = "synthetic_t1_solved______________________________________________";
    private const string T2_root      = "synthetic_t2_root________________________________________________";
    private const string T2_solved    = "synthetic_t2_solved______________________________________________";
    private const string T2_solved_alt= "synthetic_t2_solved_alt__________________________________________";
    private const string G_shared     = "synthetic_junction_shared________________________________________";  // the junction
    private const string G_t1_only    = "synthetic_t1_intermediate________________________________________";
    private const string G_t2_sub     = "synthetic_t2_subgoal____________________________________________";

    public const string Theorem1 = "T1";
    public const string Theorem2 = "T2";

    /// <summary>The set of root-goal hashes — useful for tests that want to drive episodes directly.</summary>
    public static readonly string[] RootGoals = { T1_root, T2_root };

    /// <summary>
    /// Builds the synthetic edge set + empty vectors (the chainer accepts empty embeddings;
    /// cosine-sim falls back to 0, which is fine for structural testing).
    /// </summary>
    public static (List<AppliesEdge> Edges, Dictionary<string, float[]> Vectors) Build()
    {
        var edges = new List<AppliesEdge>
        {
            // ── T1: root has an OR-choice (tactic_a vs tactic_b) ──
            // tactic_a reaches the shared junction goal.
            Edge(T1_root,   G_shared,  "tactic_a", Theorem1, "train"),
            // tactic_b reaches a T1-only intermediate.
            Edge(T1_root,   G_t1_only, "tactic_b", Theorem1, "train"),
            // Both T1 intermediates lead to the same solved state.
            Edge(G_shared,  T1_solved, "tactic_c", Theorem1, "train"),
            Edge(G_t1_only, T1_solved, "tactic_d", Theorem1, "train"),

            // ── T2: root tactic produces TWO subgoals (genuine AND-branch / n-ary) ──
            // The same tactic_e firing on T2_root produces both G_shared and G_t2_sub. This is
            // the n-ary fact that maps onto one Topos edge-vertex with two member incidences —
            // the structural motivator. Grouped under (T2_root, tactic_e) → one hyperedge.
            Edge(T2_root,   G_shared,  "tactic_e", Theorem2, "eval"),
            Edge(T2_root,   G_t2_sub,  "tactic_e", Theorem2, "eval"),
            // The shared goal also closes to a T2-specific solved state. NOTE: distinct tactic
            // name from T1's tactic_c — the grouping rule keys on (GoalBeforeHash, TacticId), so
            // if both used "tactic_c" they'd collapse into one hyperedge. tactic_g keeps them as
            // two separate hyperedges both anchored on G_shared (the genuine junction).
            Edge(G_shared,  T2_solved_alt, "tactic_g", Theorem2, "eval"),
            // The T2-specific subgoal closes to T2_solved.
            Edge(G_t2_sub,  T2_solved, "tactic_f", Theorem2, "eval"),
        };

        // Empty vectors — synthetic fixture; structural test only.
        var vectors = new Dictionary<string, float[]>(StringComparer.Ordinal);
        foreach (var e in edges)
        {
            vectors.TryAdd(e.GoalBeforeHash, []);
            vectors.TryAdd(e.GoalAfterHash, []);
        }

        return (edges, vectors);
    }

    /// <summary>Exposes the internal pseudo-hashes by name for tests that assert against specific goals.</summary>
    public static class Goals
    {
        public const string T1Root       = SyntheticAppliesGraph.T1_root;
        public const string T1Solved     = SyntheticAppliesGraph.T1_solved;
        public const string T2Root       = SyntheticAppliesGraph.T2_root;
        public const string T2Solved     = SyntheticAppliesGraph.T2_solved;
        public const string T2SolvedAlt  = SyntheticAppliesGraph.T2_solved_alt;
        public const string Shared       = SyntheticAppliesGraph.G_shared;
        public const string T1Only       = SyntheticAppliesGraph.G_t1_only;
        public const string T2Sub        = SyntheticAppliesGraph.G_t2_sub;
    }

    private static AppliesEdge Edge(string before, string after, string tactic, string theorem, string split) =>
        new(before, after, tactic, theorem, split, Count: 1, SuccessSum: 1, GoalBeforeVector: [], GoalAfterVector: []);
}
