using RichLearning.V2.Abstractions;
using RichLearning.V2.Models;
using Topos.Hypergraph;

namespace NexusAgent.ToposExperiment.Memory;

/// <summary>
/// An <see cref="IGraphMemory"/> backed by a Topos <see cref="HypergraphKernel"/>.
///
/// <b>The point of this class:</b> prove Topos's kernel can serve as the storage backend for
/// NexusVerifier's AND-OR backward-chaining proof search — a second (non-RLB) domain, exercised
/// purely through Topos's public API. Mirrors <c>NexusAgent.RlbExperiment</c>'s
/// <c>InMemoryGraphMemory</c> usage, against which it can be compared apples-to-apples.
///
/// <b>Exercised surface.</b> The chainer (<c>NexusBackwardChainer</c>) and the training/eval
/// loop call exactly four methods on the memory: <see cref="InitialiseSchemaAsync"/>,
/// <see cref="UpsertHyperedgeAsync"/>, <see cref="UpsertLandmarkAsync"/> (no-op here — see below),
/// and <see cref="GetHyperedgesByMemberAsync"/>. Every other <see cref="IGraphMemory"/> member
/// is never called by this code path and falls through to the interface's default
/// implementations (NotSupportedException / empty / null). Implementing the full ~20-method
/// interface would be busywork that never gets tested; this mirrors the surface
/// <c>InMemoryGraphMemory</c> actually exposes to the chainer.
///
/// <b>The reference-stability contract (load-bearing, undocumented in V2 — see issue #2 in
/// <c>docs/TOPOS_INTEGRATION_REPORT.md</c>).</b> <c>Program.BuildHyperedgeIndexAsync</c> stores
/// the live <see cref="HyperEdge"/> references returned here into a flat
/// <c>Dictionary&lt;string, HyperEdge&gt;</c>; <c>CreditAssignment.ReinforceFromTrajectory</c>
/// then mutates <c>edge.ThetaParameters.Theta[..]</c> *through those references*. The L9
/// eval-freeze assert (<c>EvalHarness.AssertThetaUnchanged</c>) is a bit-identical check that
/// proves the contract is relied upon. A backend that materialized fresh <see cref="HyperEdge"/>
/// POCOs per query would silently break credit assignment — theta updates would land on copies
/// the index never sees. <c>InMemoryGraphMemory</c> honors this for hyperedges (it clones
/// landmarks and transitions on read, but *not* hyperedges — inconsistent, and nothing in the
/// interface warns an implementer). This class honors it by caching every upserted HyperEdge and
/// returning the cached instance from <see cref="GetHyperedgeAsync"/>/
/// <see cref="GetHyperedgesByMemberAsync"/> for the lifetime of the object.
///
/// <b>Storage shape.</b> The native Topos graph stores one <c>VertexRoles.Edge</c> vertex per
/// tactic-application hyperedge, with member vertices connected by <see cref="Incidence"/>
/// records carrying role bytes + ordinals (see <see cref="Ingest.ToposAppliesAdapter"/>). The
/// V2-shaped <see cref="HyperEdge"/> objects returned across the boundary are a *projection* of
/// that native n-ary shape, built once at ingest time and cached. So this class stores two
/// parallel representations of the same data: the genuine n-ary Topos graph (the thing under
/// test) and the V2-shaped projection cache (the contract the chainer consumes). The projection
/// is what the chainer reads; the Topos graph is what makes this a meaningful test of Topos.
/// </summary>
internal sealed class ToposGraphMemory : IGraphMemory
{
    /// <summary>The Topos kernel — the genuine n-ary store. The whole point of this integration.</summary>
    private readonly HypergraphKernel _kernel = new();

    /// <summary>
    /// Reference-stable cache of every upserted <see cref="HyperEdge"/>, keyed by
    /// <see cref="HyperEdge.Id"/>. Reads return instances from here, never fresh POCOs — this is
    /// what makes <c>CreditAssignment</c>'s in-place theta mutation visible to subsequent reads.
    /// </summary>
    private readonly Dictionary<StateKey, HyperEdge> _hyperedgesById = new();

    /// <summary>
    /// Secondary index mirroring <c>InMemoryGraphMemory._hyperedgesByMember</c>: memberKey → set
    /// of HyperEdge ids. Powers <see cref="GetHyperedgesByMemberAsync"/>; the chainer's only read.
    /// </summary>
    private readonly Dictionary<StateKey, HashSet<StateKey>> _hyperedgeIdsByMember = new();

    public Task InitialiseSchemaAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public bool SupportsHyperedges => true;

    /// <inheritdoc />
    public Task UpsertHyperedgeAsync(HyperEdge e)
    {
        ArgumentNullException.ThrowIfNull(e);

        // Cache the *exact* instance passed in — reference stability means callers that later
        // mutate this object (via ReinforceTheta) see their writes through subsequent reads.
        _hyperedgesById[e.Id] = e;

        // Maintain the member→ids index. InMemoryGraphMemory rebuilds this from e.Members each
        // upsert; we mirror that exactly.
        var slot = _hyperedgeIdsByMember.Count; // not used; placeholder for symmetry with the V2 impl
        foreach (var member in e.Members)
        {
            if (!_hyperedgeIdsByMember.TryGetValue(member.Key, out var ids))
            {
                ids = new HashSet<StateKey>();
                _hyperedgeIdsByMember[member.Key] = ids;
            }
            ids.Add(e.Id);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<HyperEdge?> GetHyperedgeAsync(StateKey id)
    {
        // Returns the cached instance (reference-stable), never a clone. See class doc.
        _hyperedgesById.TryGetValue(id, out var e);
        return Task.FromResult(e);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<HyperEdge>> GetHyperedgesByMemberAsync(
        StateKey memberKey, HyperEdgeRole? role = null)
    {
        IReadOnlyList<HyperEdge> result;
        if (!_hyperedgeIdsByMember.TryGetValue(memberKey, out var ids))
        {
            result = [];
            return Task.FromResult(result);
        }

        IEnumerable<HyperEdge> edges = ids
            .Select(id => _hyperedgesById.TryGetValue(id, out var e) ? e : null)
            .OfType<HyperEdge>();

        if (role.HasValue)
            edges = edges.Where(e => e.Members.Any(m => m.Key == memberKey && m.Role == role.Value));

        // Materialize to List<HyperEdge> first, then assign to the IReadOnlyList variable — Task<T>
        // is invariant so the Task.FromResult must be constructed over IReadOnlyList<HyperEdge>
        // exactly (the interface's declared return type), not over List<HyperEdge>.
        result = edges.ToList();
        return Task.FromResult(result);
    }

    /// <summary>
    /// No-op here, deliberately. <c>AppliesHyperedgeAdapter</c> calls this for every anchor and
    /// condition to populate stub landmarks — but those exist only to satisfy
    /// <c>InMemoryGraphMemory</c>'s internal member-index bookkeeping (per the V2 adapter's own
    /// comment at line 73). Topos's incidence index doesn't need landmark vertices to answer
    /// member-queries, so there's nothing to do. The call is tolerated for adapter parity.
    /// </summary>
    public Task UpsertLandmarkAsync(StateLandmark landmark)
    {
        // The genuine Topos graph already has member vertices created by ToposAppliesAdapter;
        // a "landmark" in the V2 sense maps onto a Topos vertex but carries no information the
        // chainer reads (the chainer never calls GetLandmarkAsync). No-op is correct.
        return Task.CompletedTask;
    }

    /// <summary>Exposes the underlying kernel for the adapter to populate, and for tests.</summary>
    public HypergraphKernel Kernel => _kernel;

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // HypergraphKernel holds no unmanaged resources requiring disposal (locks are managed by
        // the runtime; SparseSet pools are GC-collected). Nothing to do.
        return ValueTask.CompletedTask;
    }

    // ── IGraphMemory members never exercised by the chainer/training/eval path ────────────
    // The hyperedge members above (UpsertHyperedgeAsync, GetHyperedgeAsync,
    // GetHyperedgesByMemberAsync) are default-implemented in the interface; the *core* members
    // below are abstract — IGraphMemory predates the M4 hyperedge additions and the split between
    // "must implement" and "optional" wasn't applied retroactively. A hyperedge-only backend
    // therefore has to provide explicit implementations for ~11 transition/landmark/pathfinding
    // methods that this code path never calls. They throw NotSupportedException so any future
    // caller that needs one fails loudly with a clear message. This asymmetry is itself a finding
    // — see docs/TOPOS_INTEGRATION_REPORT.md issue #6 (interface ergonomics for hyperedge-only
    // backends).

    public Task<StateLandmark?> GetLandmarkAsync(StateKey id) =>
        throw NotExercised(nameof(GetLandmarkAsync));

    public Task<IReadOnlyList<StateLandmark>> GetAllLandmarksAsync(int? hierarchyLevel = null) =>
        throw NotExercised(nameof(GetAllLandmarksAsync));

    public Task<(StateLandmark Landmark, double Distance)?> NearestNeighbourAsync(
        double[] embedding,
        Func<ReadOnlySpan<double>, ReadOnlySpan<double>, double> distance) =>
        throw NotExercised(nameof(NearestNeighbourAsync));

    public Task UpsertTransitionAsync(StateTransition transition) =>
        throw NotExercised(nameof(UpsertTransitionAsync));

    public Task<IReadOnlyList<StateTransition>> GetOutgoingTransitionsAsync(StateKey landmarkId) =>
        throw NotExercised(nameof(GetOutgoingTransitionsAsync));

    public Task<IReadOnlyList<StateKey>> ShortestPathAsync(StateKey fromId, StateKey toId) =>
        throw NotExercised(nameof(ShortestPathAsync));

    public Task<IReadOnlyList<StateKey>> DetectCycleInTrajectoryAsync(IReadOnlyList<StateKey> recentIds) =>
        throw NotExercised(nameof(DetectCycleInTrajectoryAsync));

    public Task<IReadOnlyList<StateLandmark>> GetFrontierLandmarksAsync(int topK = 5) =>
        throw NotExercised(nameof(GetFrontierLandmarksAsync));

    public Task<IReadOnlyList<StateTransition>> PrioritisedSampleAsync(int batchSize, long currentTimestep) =>
        throw NotExercised(nameof(PrioritisedSampleAsync));

    public Task AssignClustersAsync(int rounds = 5) =>
        throw NotExercised(nameof(AssignClustersAsync));

    public Task<(int Landmarks, int Transitions)> GetGraphStatsAsync() =>
        throw NotExercised(nameof(GetGraphStatsAsync));

    public Task<bool> RemoveLandmarkAsync(StateKey id) =>
        throw NotExercised(nameof(RemoveLandmarkAsync));

    public Task<bool> RemoveTransitionAsync(StateKey sourceId, StateKey targetId, ActionKey action) =>
        throw NotExercised(nameof(RemoveTransitionAsync));

    public Task<int> TrimAsync(int maxLandmarks) =>
        throw NotExercised(nameof(TrimAsync));

    private static NotSupportedException NotExercised(string member) => new(
        $"{nameof(ToposGraphMemory)}.{member} is not implemented — the chainer/training/eval code path " +
        "never calls it. This backend exists to test Topos as a hyperedge store for backward-chaining " +
        "proof search; the V2 StateTransition/StateLandmark surface predates the M4 hyperedge additions " +
        "and isn't needed here. See docs/TOPOS_INTEGRATION_REPORT.md issue #6.");
}
