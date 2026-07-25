using NexusAgent.ToposExperiment.Ingest;
using NexusAgent.ToposExperiment.Memory;
using NexusAgent.ToposExperiment.Models;
using RichLearning.V2.Abstractions;
using RichLearning.V2.Models;

namespace NexusAgent.ToposExperiment.Tests;

/// <summary>
/// The single most important contract this integration depends on: <b>reference stability of
/// returned HyperEdge objects</b>. <c>Program.BuildHyperedgeIndexAsync</c> stores the live
/// instances returned by <see cref="IGraphMemory.GetHyperedgesByMemberAsync"/> into a flat index,
/// and <c>CreditAssignment.ReinforceFromTrajectory</c> mutates
/// <c>edge.ThetaParameters.Theta[..]</c> *through those references*. If a backend returned fresh
/// POCOs per query, theta updates would land on copies the index never sees — silently breaking
/// credit assignment. The L9 eval-freeze assert would also fire.
///
/// <c>InMemoryGraphMemory</c> honors this for hyperedges (it clones landmarks and transitions on
/// read, but *not* hyperedges — inconsistent). <see cref="ToposGraphMemory"/> honors it by
/// caching. These tests pin the contract explicitly so a future refactor that introduces
/// defensive-copy-on-read fails loudly here rather than silently degrading learning.
/// </summary>
public class ReferenceStabilityTests
{
    [Fact]
    public async Task GetHyperedgeAsync_ReturnsSameInstance_AcrossCalls()
    {
        var (edges, _) = Fixtures.SyntheticAppliesGraph.Build();
        var memory = await ToposAppliesAdapter.BuildMemoryAsync(edges);

        // Pick any hyperedge id that was ingested.
        var sampleId = memory.Kernel.VertexHandles()
            .Select(h => new StateKey($"he:{edges[0].GoalBeforeHash}:{edges[0].TacticId}"))
            .First();

        var first = await memory.GetHyperedgeAsync(sampleId);
        var second = await memory.GetHyperedgeAsync(sampleId);

        Assert.NotNull(first);
        Assert.Same(first, second); // reference equality — the contract
    }

    [Fact]
    public async Task GetHyperedgesByMemberAsync_ReturnsSameInstance_ViaIndexAndDirectRead()
    {
        // This is the exact pattern Program.cs + CreditAssignment rely on: the index built from
        // GetHyperedgesByMemberAsync holds references that, when mutated, are visible to a
        // subsequent GetHyperedgeAsync / GetHyperedgesByMemberAsync call.
        var (edges, _) = Fixtures.SyntheticAppliesGraph.Build();
        var memory = await ToposAppliesAdapter.BuildMemoryAsync(edges);

        // T1_root is the anchor for tactic_a and tactic_b — two hyperedges.
        var viaMember = await memory.GetHyperedgesByMemberAsync(
            new StateKey(Fixtures.SyntheticAppliesGraph.Goals.T1Root),
            HyperEdgeRole.Anchor);

        Assert.Equal(2, viaMember.Count);

        // ReinforceTheta mutates the live edge in place. If reference-stability holds, the same
        // edges retrieved again after reinforcement will reflect the mutation.
        var ctx = new TraversalContext(0.0, 0.0);
        foreach (var edge in viaMember)
            edge.ReinforceTheta(decayedReward: 0.5, learningRate: 0.05, ctx);

        // Re-fetch and assert the theta is non-null on the *re-fetched* instances — proves the
        // mutation landed on the cached object, not a copy.
        var refetched = await memory.GetHyperedgesByMemberAsync(
            new StateKey(Fixtures.SyntheticAppliesGraph.Goals.T1Root),
            HyperEdgeRole.Anchor);

        Assert.All(refetched, edge => Assert.NotNull(edge.ThetaParameters));

        // And via GetHyperedgeAsync by id — same instances.
        foreach (var edge in viaMember)
        {
            var byId = await memory.GetHyperedgeAsync(edge.Id);
            Assert.Same(edge, byId); // the exact object ReinforceTheta mutated
            Assert.NotNull(byId!.ThetaParameters);
        }
    }

    [Fact]
    public async Task MutationViaIndex_IsVisibleToSubsequentRead()
    {
        // Mirrors the production pattern: build the flat index (as Program.cs does), mutate via
        // the index reference (as CreditAssignment does), then read via the memory and confirm
        // the mutation is visible. This is the silent-failure failure mode the contract prevents.
        var (edges, _) = Fixtures.SyntheticAppliesGraph.Build();
        var memory = await ToposAppliesAdapter.BuildMemoryAsync(edges);

        var index = await BuildIndexAsync(memory, edges);

        // Pick any edge and mutate its theta via the index reference.
        var kvp = index.First();
        var edgeFromIndex = kvp.Value;
        Assert.Null(edgeFromIndex.ThetaParameters); // before training, theta is uninitialised

        edgeFromIndex.ReinforceTheta(0.5, 0.05, new TraversalContext(0.0, 0.0));

        // Now read the same edge back from the memory — must reflect the mutation.
        var edgeFromMemory = await memory.GetHyperedgeAsync(new StateKey(kvp.Key));
        Assert.Same(edgeFromIndex, edgeFromMemory); // same instance
        Assert.NotNull(edgeFromMemory!.ThetaParameters);
    }

    /// <summary>Reproduces Program.BuildHyperedgeIndexAsync's construction — stores live refs.</summary>
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
