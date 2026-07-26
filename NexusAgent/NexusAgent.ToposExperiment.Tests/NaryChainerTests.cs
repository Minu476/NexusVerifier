using NexusAgent.ToposExperiment.Fixtures;
using NexusAgent.ToposExperiment.Ingest;
using NexusAgent.ToposExperiment.Models;
using NexusAgent.ToposExperiment.NarySearch;
using NexusAgent.ToposExperiment.Training;
using RichLearning.V2.Abstractions;
using RichLearning.V2.Memory;
using Topos.Hypergraph;

namespace NexusAgent.ToposExperiment.Tests;

/// <summary>
/// Tests for the n-ary chainer — the Topos-native rewrite that reads the genuine n-ary shape
/// directly (no V2 HyperEdge projection). These pin three things:
///
/// 1. <b>Search correctness</b> — the n-ary chainer reaches the same goals as the projected
///    chainer on the same fixture (correctness parity, the load-bearing bar).
/// 2. <b>LearnableEdge reinforcement</b> — credit assignment read-modify-writes a Topos
///    LearnableEdge through the kernel's property pool, and the update is visible to subsequent
///    reads. This is the path that dissolves integration-report issue #2 (reference stability) —
///    there's no shared mutable object, just the kernel as source of truth.
/// 3. <b>L9 eval-freeze on the kernel</b> — the n-ary eval harness snapshots every edge-vertex's
///    LearnableEdge and asserts eval doesn't mutate it.
/// </summary>
public class NaryChainerTests
{
    [Fact]
    public async Task NaryIngest_ProducesSevenTacticEdges_NoV2Projection()
    {
        // Same fixture, same grouping rule as the projected path → same 7 tactic-edges, same
        // 15 vertices, same 15 incidences. The difference is purely that no V2 HyperEdge
        // objects were created (BuildNaryAsync returns the kernel + map, not a ToposGraphMemory).
        var (edges, _) = SyntheticAppliesGraph.Build();
        var build = await ToposAppliesAdapter.BuildNaryAsync(edges);

        Assert.Equal(7, build.EdgeCount);
        Assert.Equal(15, build.Kernel.CountVertices());
        Assert.Equal(15, build.Kernel.AllIncidences().Count());
        Assert.Equal(8, build.GoalHandleByHash.Count); // 8 distinct goal-hashes
    }

    [Fact]
    public async Task Search_T1Root_SolvesInTwoSteps_MatchingProjected()
    {
        // Correctness parity: the n-ary chainer must produce the same answer as the projected
        // chainer on T1_root (2 steps: root → intermediate → leaf, via either OR-branch).
        var (edges, vectors) = SyntheticAppliesGraph.Build();
        var build = await ToposAppliesAdapter.BuildNaryAsync(edges);
        var chainer = new NaryBackwardChainer(build.Kernel, build.GoalHandleByHash, vectors);

        var dag = new TrajectoryDag();
        var result = await chainer.SearchAsync(
            SyntheticAppliesGraph.Goals.T1Root, vectors[T1RootKey()],
            NarySearchArm.BaselineN, fuel: 50, dag);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Steps); // matches the projected chainer's T1_root result
    }

    [Fact]
    public async Task Search_T2Root_SolvesViaAndBranch_MatchingProjected()
    {
        // The n-ary AND-branch: T2_root's tactic_e produces two subgoals. The chainer reads them
        // as the edge-vertex's after-role members — no Target slot, no contortion. Must match
        // the projected path's 3 steps (root + 2 leaves).
        var (edges, vectors) = SyntheticAppliesGraph.Build();
        var build = await ToposAppliesAdapter.BuildNaryAsync(edges);
        var chainer = new NaryBackwardChainer(build.Kernel, build.GoalHandleByHash, vectors);

        var dag = new TrajectoryDag();
        var result = await chainer.SearchAsync(
            SyntheticAppliesGraph.Goals.T2Root, vectors[T2RootKey()],
            NarySearchArm.BaselineN, fuel: 50, dag);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Steps); // matches the projected chainer's T2_root result
    }

    [Fact]
    public async Task Search_JunctionGoal_GShared_HasTwoCandidates_Natively()
    {
        // The junction surfaces natively: G_shared participates in tactic_a (T1) and tactic_g (T2)
        // as a before-member. CandidateEdgesFor must return both edge-vertices with no projection.
        var (edges, _) = SyntheticAppliesGraph.Build();
        var build = await ToposAppliesAdapter.BuildNaryAsync(edges);
        var kernel = build.Kernel;
        var gSharedHandle = build.GoalHandleByHash[SyntheticAppliesGraph.Goals.Shared];

        // Reproduce the chainer's candidate-finding logic against the kernel directly.
        var candidates = new List<Handle>();
        foreach (var edge in kernel.GetVertexHyperedges(gSharedHandle))
            foreach (var inc in kernel.IncidencesOf(gSharedHandle))
                if (inc.Source == edge && inc.Role == ToposAppliesAdapter.BeforeRole)
                {
                    candidates.Add(edge);
                    break;
                }

        Assert.Equal(2, candidates.Count); // the junction, read natively
    }

    [Fact]
    public async Task Search_UnknownGoal_TreatedAsLeaf()
    {
        var (edges, vectors) = SyntheticAppliesGraph.Build();
        var build = await ToposAppliesAdapter.BuildNaryAsync(edges);
        var chainer = new NaryBackwardChainer(build.Kernel, build.GoalHandleByHash, vectors);

        var dag = new TrajectoryDag();
        var result = await chainer.SearchAsync(
            "synthetic_unknown_goal_no_vertex_______________________________",
            [], NarySearchArm.BaselineN, fuel: 50, dag);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Steps); // leaf
    }

    [Fact]
    public async Task Search_FuelExhaustion_ReturnsTimeout()
    {
        var (edges, vectors) = SyntheticAppliesGraph.Build();
        var build = await ToposAppliesAdapter.BuildNaryAsync(edges);
        var chainer = new NaryBackwardChainer(build.Kernel, build.GoalHandleByHash, vectors);

        var dag = new TrajectoryDag();
        var result = await chainer.SearchAsync(
            SyntheticAppliesGraph.Goals.T1Root, vectors[T1RootKey()],
            NarySearchArm.BaselineN, fuel: 0, dag);

        Assert.False(result.IsSuccess);
        Assert.Equal(NaryProofSearchOutcome.Timeout, result.Outcome);
    }

    [Fact]
    public async Task LearnableEdge_Reinforcement_RoundTripsThroughKernel()
    {
        // Issue #2 dissolution: credit assignment does GetProperty → LearnableEdge.Reinforce →
        // SetProperty. A subsequent read must see the reinforced value. No reference-stability
        // contract involved — the kernel is the source of truth.
        var (edges, vectors) = SyntheticAppliesGraph.Build();
        var build = await ToposAppliesAdapter.BuildNaryAsync(edges);
        var kernel = build.Kernel;

        var chainer = new NaryBackwardChainer(kernel, build.GoalHandleByHash, vectors);
        var trainingLoop = new NaryTrainingLoop(chainer, kernel);
        var trainRoots = new List<GoalEntry>
        {
            new(SyntheticAppliesGraph.Theorem1, SyntheticAppliesGraph.Goals.T1Root, []),
        };

        await trainingLoop.RunAsync(trainRoots, episodes: 5, seed: 42, fuel: 50);

        // After training, at least one edge-vertex must have a non-default LearnableEdge.
        var learnableProp = kernel.ResolveProperty<LearnableEdge>("learnable");
        int trainedCount = kernel.VertexHandles()
            .Where(h => kernel.TryGetVertex(h, out var v) && v.Roles == VertexRoles.Edge)
            .Count(h => kernel.TryGetProperty(learnableProp, h, out _));

        Assert.True(trainedCount > 0, "Training must reinforce at least one edge-vertex's LearnableEdge.");

        // And the reinforced theta must be non-zero (the default LearnableEdge.CreateUninitialized
        // is all-zero; Reinforce adds a gradient step). Pick any trained edge and check.
        var trainedEdge = kernel.VertexHandles()
            .First(h => kernel.TryGetVertex(h, out var v) && v.Roles == VertexRoles.Edge
                        && kernel.TryGetProperty(learnableProp, h, out _));
        kernel.TryGetProperty(learnableProp, trainedEdge, out var le);
        Assert.True(le.Theta.Any(t => t != 0f), "Reinforced theta must be non-zero somewhere.");
    }

    [Fact]
    public async Task NaryEvalHarness_L9FreezeAssert_PassesAfterTraining()
    {
        // The n-ary L9 canary: snapshots every edge-vertex's LearnableEdge, runs eval, asserts
        // nothing changed. Because LearnableEdge is immutable and the kernel is the source of
        // truth, this is structurally simpler than the projected path's reference-identity check.
        var (edges, vectors) = SyntheticAppliesGraph.Build();
        var build = await ToposAppliesAdapter.BuildNaryAsync(edges);
        var kernel = build.Kernel;

        var chainer = new NaryBackwardChainer(kernel, build.GoalHandleByHash, vectors);
        var trainingLoop = new NaryTrainingLoop(chainer, kernel);
        var trainRoots = new List<GoalEntry>
        {
            new(SyntheticAppliesGraph.Theorem1, SyntheticAppliesGraph.Goals.T1Root, []),
        };
        await trainingLoop.RunAsync(trainRoots, episodes: 5, seed: 42, fuel: 50);

        // Eval on a different root (T2). If L9 freezes correctly, RunAsync returns normally.
        var evalRoots = new List<GoalEntry>
        {
            new(SyntheticAppliesGraph.Theorem2, SyntheticAppliesGraph.Goals.T2Root, []),
        };
        var harness = new NaryEvalHarness(chainer, kernel);
        var summary = await harness.RunAsync(evalRoots, fuel: 50);

        Assert.True(summary.TrainedHyperEdges > 0); // sanity: training did happen
    }

    private static string T1RootKey() => SyntheticAppliesGraph.Goals.T1Root;
    private static string T2RootKey() => SyntheticAppliesGraph.Goals.T2Root;
}

/// <summary>
/// Cross-path parity: the n-ary and projected chainers must produce identical solve outcomes on
/// the same fixture. This is the integration's correctness bar — if storage faithfulness holds,
/// the search algorithm reaches the same goals regardless of whether it reads the genuine n-ary
/// shape or the V2-projected shape. (Theta values legitimately differ — the two paths use
/// different theta representations — but solve rate and step counts must match.)
/// </summary>
public class NaryProjectedParityTests
{
    [Theory]
    [InlineData(SyntheticAppliesGraph.Theorem1, "T1Root")]
    [InlineData(SyntheticAppliesGraph.Theorem2, "T2Root")]
    public async Task NaryAndProjected_ProduceIdenticalSolveOutcomes(string theorem, string goalKey)
    {
        var (edges, vectors) = SyntheticAppliesGraph.Build();
        var goalHash = goalKey switch
        {
            "T1Root" => SyntheticAppliesGraph.Goals.T1Root,
            "T2Root" => SyntheticAppliesGraph.Goals.T2Root,
            _ => throw new ArgumentException($"unknown goal key {goalKey}"),
        };

        // ── Projected path ──
        var projectedMemory = await ToposAppliesAdapter.BuildMemoryAsync(edges);
        var projectedChainer = new Search.NexusBackwardChainer(projectedMemory, vectors);
        var projectedDag = new TrajectoryDag();
        var projectedResult = await projectedChainer.SearchAsync(
            goalHash, vectors[goalHash], Search.SearchArm.BaselineN, fuel: 50, projectedDag);

        // ── N-ary path ──
        var naryBuild = await ToposAppliesAdapter.BuildNaryAsync(edges);
        var naryChainer = new NaryBackwardChainer(naryBuild.Kernel, naryBuild.GoalHandleByHash, vectors);
        var naryDag = new TrajectoryDag();
        var naryResult = await naryChainer.SearchAsync(
            goalHash, vectors[goalHash], NarySearchArm.BaselineN, fuel: 50, naryDag);

        // The correctness bar: identical solve outcome + identical step count.
        Assert.Equal(projectedResult.IsSuccess, naryResult.IsSuccess);
        Assert.Equal(projectedResult.Steps, naryResult.Steps);
    }
}
