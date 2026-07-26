using NexusAgent.ToposExperiment.Ingest;
using NexusAgent.ToposExperiment.Memory;
using NexusAgent.ToposExperiment.Models;
using NexusAgent.ToposExperiment.Search;
using NexusAgent.ToposExperiment.Fixtures;
using RichLearning.V2.Abstractions;
using RichLearning.V2.Memory;
using RichLearning.V2.Models;

namespace NexusAgent.ToposExperiment.Tests;

/// <summary>
/// Hand-computed AND-OR backward-chain correctness over the synthetic fixture. This is the
/// primary structural gate: if Topos stores the graph correctly and the projection exposes it
/// faithfully, the chainer must produce the hand-computed answers. The fixture is deliberately
/// small and fully-known so expectations are by-hand, not by-run.
///
/// <b>Fixture topology (see SyntheticAppliesGraph.cs for the full picture):</b>
///   T1_root ─tactic_a→ G_shared ─tactic_c→ T1_solved (leaf)        [OR-branch: two tactics]
///           ─tactic_b→ G_t1_only ─tactic_d→ T1_solved (leaf)
///   T2_root ─tactic_e→ {G_shared, G_t2_sub}                         [AND-branch: n-ary]
///           G_shared ─tactic_c→ T2_solved_alt (leaf)               [junction reuse]
///           G_t2_sub ─tactic_f→ T2_solved (leaf)
///
/// Hand-computed:
///   T1_root solves in 2 steps (any OR-branch: root → intermediate → leaf).
///   T2_root solves in 2 steps (root → AND{both subgoals close at leaves}).
///   G_shared is a junction: 2 hyperedges share it as anchor (tactic_c appears in T1 AND T2).
/// </summary>
public class SyntheticAppliesGraphTests
{
    [Fact]
    public async Task Ingest_ProducesSevenHyperedges_AndDeduplicatesGoalVertices()
    {
        // 8 raw APPLIES edges → 7 hyperedges (tactic_e's two rows group into one n-ary edge;
        // the other 6 edges are each their own (before, tactic) group). Distinct goals:
        // T1_root, T2_root, T1_solved, T2_solved, T2_solved_alt, G_shared, G_t1_only, G_t2_sub
        // = 8 goal vertices + 7 edge-vertices = 15 total Topos vertices.
        var (edges, _) = SyntheticAppliesGraph.Build();
        var memory = await ToposAppliesAdapter.BuildMemoryAsync(edges);

        Assert.Equal(7, (await memory.GetGraphStatsAsync_orThrow()).hyperedgeCount);
        Assert.Equal(15, memory.Kernel.CountVertices());

        // The AND-branch hyperedge (tactic_e) has TWO condition members — this is the n-ary fact.
        var t2Hyperedges = await memory.GetHyperedgesByMemberAsync(
            new StateKey(SyntheticAppliesGraph.Goals.T2Root), HyperEdgeRole.Anchor);
        var tacticE = Assert.Single(t2Hyperedges);
        var conditions = tacticE.Conditions.ToList();
        Assert.Equal(2, conditions.Count); // G_shared + G_t2_sub — the n-ary AND-branch
    }

    [Fact]
    public async Task Search_T1Root_SolvesInTwoSteps_EitherOrBranch()
    {
        var (edges, vectors) = SyntheticAppliesGraph.Build();
        var memory = await ToposAppliesAdapter.BuildMemoryAsync(edges);
        var chainer = new NexusBackwardChainer(memory, vectors);

        var dag = new TrajectoryDag();
        var result = await chainer.SearchAsync(
            SyntheticAppliesGraph.Goals.T1Root, vectors[T1RootKey()], SearchArm.BaselineN,
            fuel: 50, dag);

        Assert.True(result.IsSuccess, "T1_root must solve — both OR-branches lead to a leaf.");
        Assert.Equal(2, result.Steps); // root → intermediate → leaf
    }

    [Fact]
    public async Task Search_T2Root_SolvesViaAndBranch_NAryTactic()
    {
        // T2_root's only tactic (tactic_e) produces TWO subgoals — a genuine AND-branch. The
        // chainer must recurse into BOTH and find both close at leaves. This is the case that
        // exercises Topos's n-ary storage: one edge-vertex with two member incidences.
        var (edges, vectors) = SyntheticAppliesGraph.Build();
        var memory = await ToposAppliesAdapter.BuildMemoryAsync(edges);
        var chainer = new NexusBackwardChainer(memory, vectors);

        var dag = new TrajectoryDag();
        var result = await chainer.SearchAsync(
            SyntheticAppliesGraph.Goals.T2Root, vectors[T2RootKey()], SearchArm.BaselineN,
            fuel: 50, dag);

        Assert.True(result.IsSuccess, "T2_root must solve via the AND-branch.");
        // Steps: 1 (root expansion) + 1 (G_shared closes at T2_solved_alt leaf via tactic_c) +
        //        1 (G_t2_sub closes at T2_solved leaf via tactic_f) = 3.
        // The chainer counts the root expansion as 1 step and each sub-result's Steps; a leaf
        // contributes 0. So: root=1, then both AND children each expand once (1+1) → total 3.
        Assert.Equal(3, result.Steps);
    }

    [Fact]
    public async Task Search_JunctionGoal_GShared_HasTwoCandidateEdges()
    {
        // The structural property FC100 lacked: a real junction. G_shared is the anchor for
        // tactic_c in BOTH T1 (→T1_solved) and T2 (→T2_solved_alt). Querying it must return 2
        // candidate hyperedges — this is what makes OR-branching meaningful.
        var (edges, _) = SyntheticAppliesGraph.Build();
        var memory = await ToposAppliesAdapter.BuildMemoryAsync(edges);

        var candidates = await memory.GetHyperedgesByMemberAsync(
            new StateKey(SyntheticAppliesGraph.Goals.Shared), HyperEdgeRole.Anchor);

        Assert.Equal(2, candidates.Count); // the junction: tactic_c appears in T1 and T2
    }

    [Fact]
    public async Task Search_UnknownGoal_TreatedAsLeaf_SolvesAtZeroSteps()
    {
        // A goal with no outgoing hyperedges is a proved leaf (the chainer's design). A
        // goal-hash that never appears as a GoalBefore in the fixture has no candidates → leaf.
        var (edges, vectors) = SyntheticAppliesGraph.Build();
        var memory = await ToposAppliesAdapter.BuildMemoryAsync(edges);
        var chainer = new NexusBackwardChainer(memory, vectors);

        var dag = new TrajectoryDag();
        var result = await chainer.SearchAsync(
            "synthetic_unknown_goal_no_outgoing_edges____________________________",
            [], SearchArm.BaselineN, fuel: 50, dag);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Steps); // leaf, no expansion
    }

    [Fact]
    public async Task Search_FuelExhaustion_ReturnsTimeout_NotHang()
    {
        // fuel=0 with a goal that has candidates → immediate timeout, no recursion. Guards
        // against the chainer spinning on a deep graph.
        var (edges, vectors) = SyntheticAppliesGraph.Build();
        var memory = await ToposAppliesAdapter.BuildMemoryAsync(edges);
        var chainer = new NexusBackwardChainer(memory, vectors);

        var dag = new TrajectoryDag();
        var result = await chainer.SearchAsync(
            SyntheticAppliesGraph.Goals.T1Root, vectors[T1RootKey()], SearchArm.BaselineN,
            fuel: 0, dag);

        Assert.False(result.IsSuccess);
        Assert.Equal(ProofSearchOutcome.Timeout, result.Outcome);
    }

    private static string T1RootKey() => SyntheticAppliesGraph.Goals.T1Root;
    private static string T2RootKey() => SyntheticAppliesGraph.Goals.T2Root;
}

/// <summary>Small extension so tests can ask the memory for its hyperedge count without the
/// full IGraphMemory surface (GetGraphStatsAsync throws NotSupported on ToposGraphMemory).</summary>
internal static class TestExtensions
{
    public static async Task<(int hyperedgeCount, int _)> GetGraphStatsAsync_orThrow(
        this ToposGraphMemory memory)
    {
        // Count via the internal cache by reflecting on anchors we know about — or just count
        // distinct hyperedge ids across all members we ingested. Simpler: query every goal in the
        // fixture and union the results.
        var all = new HashSet<StateKey>();
        foreach (var goal in new[]
        {
            SyntheticAppliesGraph.Goals.T1Root, SyntheticAppliesGraph.Goals.T2Root,
            SyntheticAppliesGraph.Goals.Shared, SyntheticAppliesGraph.Goals.T1Only,
            SyntheticAppliesGraph.Goals.T2Sub,
        })
        {
            var found = await memory.GetHyperedgesByMemberAsync(new StateKey(goal), HyperEdgeRole.Anchor);
            foreach (var he in found) all.Add(he.Id);
        }
        await Task.CompletedTask;
        return (all.Count, 0);
    }
}
