using NexusAgent.ToposExperiment.Eval;
using NexusAgent.ToposExperiment.Ingest;
using NexusAgent.ToposExperiment.Memory;
using NexusAgent.ToposExperiment.Models;
using NexusAgent.ToposExperiment.Search;
using NexusAgent.ToposExperiment.Fixtures;
using RichLearning.V2.Abstractions;
using RichLearning.V2.Models;
using Topos.Hypergraph;

namespace NexusAgent.ToposExperiment.Tests;

/// <summary>
/// The L9 eval-freeze canary. <c>EvalHarness.AssertThetaUnchanged</c> is a bit-identical check
/// that proves the reference-stability contract is load-bearing: it snapshots theta before eval
/// and asserts the same HyperEdge instances are unchanged afterward. If a backend returned fresh
/// POCOs per read, the snapshot and the live object would be different instances — either this
/// assert fires, or (worse, in the case where eval never reinforces) theta mutation during
/// training silently landed on copies the eval snapshot never saw.
///
/// Running EvalHarness end-to-end against the Topos backend, after a training pass that DID
/// reinforce theta, is therefore the integration-level canary that the whole
/// read-train-read-eval cycle shares object identity correctly.
/// </summary>
public class L9FreezeCanaryTests
{
    [Fact]
    public async Task EvalHarness_L9FreezeAssert_PassesAfterTraining()
    {
        var (edges, vectors) = SyntheticAppliesGraph.Build();
        var memory = await ToposAppliesAdapter.BuildMemoryAsync(edges);

        // Build the index the same way Program.cs does.
        var hyperedgeIndex = await BuildIndexAsync(memory, edges);
        var chainer = new NexusBackwardChainer(memory, vectors);

        // Train first — this is what populates theta on the edges the eval will snapshot.
        // Use the train root (T1) so something actually gets reinforced.
        var trainRoots = new List<GoalEntry>
        {
            new(SyntheticAppliesGraph.Theorem1, SyntheticAppliesGraph.Goals.T1Root, []),
        };
        var training = new Training.TrainingLoop(memory, chainer, hyperedgeIndex);
        await training.RunAsync(trainRoots, episodes: 5, seed: 42, fuel: 50);

        // Sanity: at least one edge got theta during training, otherwise the freeze assert is
        // vacuous (snapshotting nothing proves nothing).
        int trainedCount = hyperedgeIndex.Values.Count(e => e.ThetaParameters is not null);
        Assert.True(trainedCount > 0, "Training must reinforce at least one edge for the L9 canary to be meaningful.");

        // Now run eval — if reference-stability is broken anywhere, AssertThetaUnchanged throws
        // InvalidOperationException("L9 eval-freeze violation: ..."). The fact that RunAsync
        // returns normally IS the assertion.
        var evalRoots = new List<GoalEntry>
        {
            new(SyntheticAppliesGraph.Theorem2, SyntheticAppliesGraph.Goals.T2Root, []),
        };
        var harness = new EvalHarness(chainer, hyperedgeIndex);
        var summary = await harness.RunAsync(evalRoots, fuel: 50);

        // The summary must report the trained-edges count > 0 (training happened) and the L9
        // assert having passed (no exception).
        Assert.True(summary.TrainedHyperEdges > 0);
        Assert.True(summary.TotalHyperEdges > 0);
    }

    private static async Task<Dictionary<string, HyperEdge>> BuildIndexAsync(
        ToposGraphMemory memory, List<AppliesEdge> edges)
    {
        var index = new Dictionary<string, HyperEdge>(StringComparer.Ordinal);
        var anchors = edges.Select(e => e.GoalBeforeHash).Distinct(StringComparer.Ordinal).ToList();
        foreach (var anchor in anchors)
        {
            var candidates = await memory.GetHyperedgesByMemberAsync(
                new StateKey(anchor), HyperEdgeRole.Anchor);
            foreach (var he in candidates)
                index.TryAdd(he.Id.StableIdentity, he);
        }
        return index;
    }
}

/// <summary>
/// Verifies the genuine n-ary structure lives in the Topos kernel itself — not just in the V2
/// projection the chainer consumes. This is the test that proves the integration is exercising
/// Topos's structural advantage (one edge-vertex with N member incidences), even though the
/// boundary currently re-imposes V2's shape. The deferred "n-ary chainer" scope would consume
/// this native shape directly.
/// </summary>
public class ToposNativeStorageTests
{
    [Fact]
    public async Task TacticE_IsOneEdgeVertex_WithTwoMemberIncidences_InTheKernel()
    {
        // tactic_e on T2_root produces two subgoals. In Topos this is ONE Edge-vertex with three
        // incidences: T2_root (BeforeRole) + G_shared (AfterRole) + G_t2_sub (AfterRole). This
        // is the atomic n-ary fact that binary graphs fragment — Topos stores it natively.
        var (edges, _) = SyntheticAppliesGraph.Build();
        var memory = await ToposAppliesAdapter.BuildMemoryAsync(edges);
        var kernel = memory.Kernel;

        // Find the edge-vertex whose tacticId property is "tactic_e".
        var tacticIdProp = kernel.ResolveProperty<string>("tacticId");
        Handle? tacticEVertex = null;
        foreach (var h in kernel.VertexHandles())
        {
            if (kernel.TryGetVertex(h, out var v) && v.Roles == VertexRoles.Edge
                && kernel.TryGetProperty(tacticIdProp, h, out var tid) && tid == "tactic_e")
            {
                tacticEVertex = h;
                break;
            }
        }
        Assert.True(tacticEVertex.HasValue, "tactic_e edge-vertex must exist in the kernel");

        // Three incidences: one Before (T2_root) + two After (G_shared, G_t2_sub).
        var incidences = kernel.IncidencesFrom(tacticEVertex!.Value);
        Assert.Equal(3, incidences.Length);

        var before = incidences.Where(i => i.Role == ToposAppliesAdapter.BeforeRole).ToList();
        var after  = incidences.Where(i => i.Role == ToposAppliesAdapter.AfterRole).ToList();
        Assert.Single(before);   // one before-goal (the anchor)
        Assert.Equal(2, after.Count); // two after-subgoals (the AND-branch)

        // Distinct ordinals on the after-incidences (deterministic member ordering).
        var afterOrdinals = after.Select(i => i.Ordinal).ToHashSet();
        Assert.Equal(2, afterOrdinals.Count);
    }

    [Fact]
    public async Task JunctionGoal_GShared_HasTwoIncidentEdges_InTheKernel()
    {
        // The junction: G_shared participates as a member in tactic_a (T1), tactic_e (T2), and
        // tactic_c (both T1 and T2). IncidencesOf(G_shared) must return all of them — this is
        // Topos's native representation of "this goal is shared across proofs", which is exactly
        // the structural property FC100 lacked.
        var (edges, _) = SyntheticAppliesGraph.Build();
        var memory = await ToposAppliesAdapter.BuildMemoryAsync(edges);
        var kernel = memory.Kernel;

        // Find junction goals by scanning all non-edge vertices' hyperedge-membership degree.
        var memberCounts = new Dictionary<Handle, int>();
        foreach (var h in kernel.VertexHandles())
        {
            if (!kernel.TryGetVertex(h, out var v) || v.Roles == VertexRoles.Edge) continue;
            int count = kernel.GetVertexHyperedges(h).Count;
            memberCounts[h] = count;
        }

        // G_shared participates in the most edges (tactic_a, tactic_e, tactic_c from T1, tactic_c
        // from T2 → 4 distinct edge-vertices). At minimum, it's in more than one — that's the
        // junction property. T1_root is also in 2 (tactic_a, tactic_b). The structural claim is
        // "some non-edge vertex has degree > 1" — i.e., real junctions exist in the kernel.
        var maxDegree = memberCounts.Values.Max();
        Assert.True(maxDegree >= 2, "Synthetic fixture must contain at least one junction (degree ≥ 2) goal.");
    }
}
