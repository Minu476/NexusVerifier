using NexusAgent.ToposExperiment.Fixtures;
using NexusAgent.ToposExperiment.Ingest;
using NexusAgent.ToposExperiment.NarySearch;
using Topos.Hypergraph;

namespace NexusAgent.ToposExperiment.Tests;

/// <summary>
/// Task 4 of docs/SESSION_HANDOFF_2026-08-03.md: adopting Topos's <c>DirectedScc</c> (M11 phase
/// 1, external/Topos bumped 2026-08-04) as <see cref="NaryBackwardChainer.DetectStructuralCycles"/>
/// — a whole-graph structural-cycle diagnostic over the Before→After (Anchor→Target)
/// goal-dependency adjacency. See that method's doc comment for why this is additive to, not a
/// replacement of, the chainer's existing per-DFS-path <c>pathVisited</c> runtime guard.
/// </summary>
public sealed class StructuralCycleDetectionTests
{
    [Fact]
    public async Task DetectStructuralCycles_RealSyntheticFixture_FindsNone()
    {
        // The existing SyntheticAppliesGraph fixture (used by every other chainer test) is
        // acyclic by construction — a real, already-tested goal-dependency graph with no
        // structural cycle should report none.
        var (edges, _) = SyntheticAppliesGraph.Build();
        var build = await ToposAppliesAdapter.BuildNaryAsync(edges);
        var chainer = new NaryBackwardChainer(
            build.Kernel, build.GoalHandleByHash, new Dictionary<string, float[]>());

        var cycles = chainer.DetectStructuralCycles();

        Assert.Empty(cycles);
    }

    [Fact]
    public void DetectStructuralCycles_GenuineTwoGoalCycle_FindsTheWholeComponent()
    {
        // Hand-built: goal A's tactic produces subgoal B; goal B's own tactic produces subgoal
        // A back. This is a genuine mutual dependency in the Before→After adjacency — the exact
        // bug class finding #4 (docs/NEXUS_VERIFIER_INTEGRATION_FINDINGS.md, in the Topos repo)
        // describes: a goal ultimately "provable" only by assuming itself.
        var kernel = new HypergraphKernel();
        var goalA = kernel.CreateVertex();
        var goalB = kernel.CreateVertex();

        var tacticAtoB = kernel.CreateVertex(VertexRoles.Edge);
        kernel.AddIncidence(tacticAtoB, goalA, ToposAppliesAdapter.BeforeRole, ordinal: 0);
        kernel.AddIncidence(tacticAtoB, goalB, ToposAppliesAdapter.AfterRole, ordinal: 1);

        var tacticBtoA = kernel.CreateVertex(VertexRoles.Edge);
        kernel.AddIncidence(tacticBtoA, goalB, ToposAppliesAdapter.BeforeRole, ordinal: 0);
        kernel.AddIncidence(tacticBtoA, goalA, ToposAppliesAdapter.AfterRole, ordinal: 1);

        var chainer = new NaryBackwardChainer(
            kernel, new Dictionary<string, Handle>(), new Dictionary<string, float[]>());

        var cycles = chainer.DetectStructuralCycles();

        Assert.Single(cycles);
        Assert.Equal(2, cycles[0].Count);
        Assert.Contains(goalA, cycles[0]);
        Assert.Contains(goalB, cycles[0]);
    }

    [Fact]
    public void DetectStructuralCycles_AcyclicChain_FindsNone()
    {
        // A -> B -> C, no cycle: each tactic-edge fires strictly forward, nothing ever depends
        // back on an earlier goal in the chain.
        var kernel = new HypergraphKernel();
        var goalA = kernel.CreateVertex();
        var goalB = kernel.CreateVertex();
        var goalC = kernel.CreateVertex();

        var tacticAtoB = kernel.CreateVertex(VertexRoles.Edge);
        kernel.AddIncidence(tacticAtoB, goalA, ToposAppliesAdapter.BeforeRole, ordinal: 0);
        kernel.AddIncidence(tacticAtoB, goalB, ToposAppliesAdapter.AfterRole, ordinal: 1);

        var tacticBtoC = kernel.CreateVertex(VertexRoles.Edge);
        kernel.AddIncidence(tacticBtoC, goalB, ToposAppliesAdapter.BeforeRole, ordinal: 0);
        kernel.AddIncidence(tacticBtoC, goalC, ToposAppliesAdapter.AfterRole, ordinal: 1);

        var chainer = new NaryBackwardChainer(
            kernel, new Dictionary<string, Handle>(), new Dictionary<string, float[]>());

        Assert.Empty(chainer.DetectStructuralCycles());
    }
}
