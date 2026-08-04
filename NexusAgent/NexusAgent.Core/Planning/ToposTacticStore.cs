using Microsoft.Extensions.Logging;
using NexusAgent.Core.Memory;
using NexusAgent.Core.Models;
using Topos.Hypergraph;

namespace NexusAgent.Core.Planning;

/// <summary>
/// Topos-native cross-run tactic store. See <see cref="IToposTacticStore"/> for the contract.
///
/// <b>Storage model</b> (the n-ary tactic-application shape proven by
/// <c>ToposAppliesAdapter</c> and the ChatMemory sample):
///   • Goal vertices: regular vertices keyed by a SHA-256 goal hash, carrying the 64-dim
///     <c>stateVector</c> (<c>PropertyKey&lt;float[]&gt;</c>) that <c>VectorIndex</c> searches over.
///   • Tactic edge-vertices: one <c>VertexRoles.Edge</c> per (goalHash, tacticId) group, with
///     one <see cref="BeforeRole"/> incidence to its goal, and properties: tacticId, tacticText,
///     <c>EdgeStatistics</c> (success EMA). Per-edge-vertex (not per-incidence) because Topos's
///     per-incidence cell-property addressing isn't built (integration finding #1) — the
///     per-edge workaround is the documented, blessed pattern.
///
/// <b>Retrieval</b>: <see cref="ProposeAsync"/> runs <c>VectorIndex.NearestNeighbors</c> over
/// goal vectors, then for each nearby goal walks its <c>GetVertexHyperedges</c> to find tactic
/// edges, rank-mixing similarity (0.65) × success (0.25) × log support (0.10) — the same formula
/// <c>Neo4jClient.ProposeTacticsFromGoalVectorAsync</c> uses, so ranking behaviour is comparable.
///
/// <b>Learning</b>: <see cref="RecordOutcomeAsync"/> does the read-modify-write over
/// <c>EdgeStatistics</c> (read current → <c>Observe(succeeded)</c> → <c>SetProperty</c> back).
/// This closes the feedback loop the Neo4j path never had: a tactic that worked on problem N
/// ranks higher for a similar goal on problem N+1; one that failed decays.
///
/// <b>Concurrency</b>: <c>HypergraphKernel</c> is single-writer (SWMR). The bench runs 12
/// problems in parallel, all writing outcomes to this one singleton store. Writes
/// (<c>RecordOutcomeAsync</c>, <c>SeedFromFossilsAsync</c>) are serialized behind a lock;
/// <c>ProposeAsync</c>'s kernel/<c>_vectorIndex</c> reads are safe under the kernel's reader
/// concurrency and need no lock. <see cref="GoalCount"/>/<see cref="TacticEdgeCount"/> are the
/// one exception: they read the plain (non-thread-safe) dedup dictionaries the write paths
/// mutate under the same lock, so they take it too, internally.
/// </summary>
public sealed class ToposTacticStore : IToposTacticStore
{
    // Role bytes — adapter-owned, per Topos's "kernel does not judge" design. Same convention
    // as ToposAppliesAdapter.BeforeRole/AfterRole and ProofGoalGraph.BeforeRole/AfterRole.
    private const byte BeforeRole = 0;  // the goal-state the tactic is applied to
    private const byte AfterRole = 1;   // (reserved for future after-states; not populated today)

    // Rank-mix weights — identical to Neo4jClient.ProposeTacticsFromGoalVectorAsync so the
    // Topos and Neo4j retrieval paths are comparable on the same ranking formula.
    private const float SimWeight = 0.65f;
    private const float SuccessWeight = 0.25f;
    private const float SupportWeight = 0.10f;

    // ── Quality gate (the lesson from the 2026-08-03 ungated rerun) ──────────────
    // A cold/sparse store returns false-positive neighbors: with few goal vectors, the nearest
    // is always "similar" (sim≈0.97) even when structurally unrelated, and the rank-mix (which
    // weights similarity at 0.65) over-weights that false similarity. The ungated store proposed
    // 65 times, won once, and DISPLACED LLM turns that would have solved 3 problems (13→10).
    // Gate so a sparse/uncertain store stays silent (reverts to baseline behavior) instead of
    // injecting noise. These thresholds are deliberately conservative; relax only with evidence.
    private const int MinEdgesToPropose = 50;    // skip Tier 0.75 entirely below this
    private const float MinSimilarity = 0.92f;    // cosine; below = structurally different goal
    private const float MinSuccessRate = 0.5f;    // only tactics that have actually worked

    private readonly HypergraphKernel _kernel = new();
    private readonly ILogger<ToposTacticStore> _log;
    private readonly object _writeLock = new();  // serializes all kernel mutations (SWMR)

    // Typed property keys — resolved once at construction (O(1) pool lookup thereafter).
    private readonly PropertyKey<float[]> _stateVectorProp;
    private readonly PropertyKey<string> _goalHashProp;
    private readonly PropertyKey<string> _goalTextProp;
    private readonly PropertyKey<string> _tacticIdProp;
    private readonly PropertyKey<string> _tacticTextProp;
    private readonly PropertyKey<EdgeStatistics> _statsProp;

    // Goal-hash → vertex Handle dedup map. One vertex per distinct normalized goal.
    private readonly Dictionary<string, Handle> _goalVertexByHash = new(StringComparer.Ordinal);
    // (goalHash, tacticId) → edge-vertex Handle dedup map.
    private readonly Dictionary<(string GoalHash, string TacticId), Handle> _edgeByGoalTactic = new();

    // One VectorIndex instance held for the store's lifetime, not rebuilt. NearestNeighbors
    // does a fresh O(V) scan over EnumerateProperty each call -- no cached structure to
    // invalidate -- so a single instance reads live from the kernel and stays correct across
    // mutations with no rebuild step needed.
    private readonly VectorIndex _vectorIndex;

    // _goalVertexByHash/_edgeByGoalTactic are plain Dictionary<>s, not thread-safe. Mutations
    // (GetOrAddGoalVertex, RecordSuccessAsync, SeedFromFossilsAsync) already run under
    // _writeLock; these Count reads must take the same lock -- the bench runs problems in
    // parallel against this one singleton store, so an unlocked .Count read here races against
    // a concurrent dictionary insert on another thread.
    public int GoalCount { get { lock (_writeLock) { return _goalVertexByHash.Count; } } }
    public int TacticEdgeCount { get { lock (_writeLock) { return _edgeByGoalTactic.Count; } } }

    public ToposTacticStore(ILogger<ToposTacticStore> log)
    {
        _log = log;
        _stateVectorProp = _kernel.ResolveProperty<float[]>("stateVector");
        _goalHashProp    = _kernel.ResolveProperty<string>("goalHash");
        _goalTextProp    = _kernel.ResolveProperty<string>("goalText");
        _tacticIdProp    = _kernel.ResolveProperty<string>("tacticId");
        _tacticTextProp  = _kernel.ResolveProperty<string>("tacticText");
        _statsProp       = _kernel.ResolveProperty<EdgeStatistics>("stats");
        _vectorIndex     = new VectorIndex(_kernel, _stateVectorProp);
    }

    public Task<IReadOnlyList<GraphTacticProposal>> ProposeAsync(
        float[] queryVector, int neighborK, int topK, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Quality gate #1: a cold/sparse store stays silent. Below MinEdgesToPropose, the
        // VectorIndex has no discriminative power (nearest-of-N is always "close"), so
        // proposing would inject false positives that displace LLM turns. Revert to baseline.
        if (TacticEdgeCount < MinEdgesToPropose)
        {
            _log.LogDebug(
                "ToposTacticStore.Propose: silent (cold store: {Edges} edges < {Min})",
                TacticEdgeCount, MinEdgesToPropose);
            return Task.FromResult<IReadOnlyList<GraphTacticProposal>>(Array.Empty<GraphTacticProposal>());
        }

        // VectorIndex reads live from the kernel (no cached snapshot to invalidate under SWMR
        // reader concurrency — EnumerateProperty is a read-only scan). No lock needed for reads.
        var neighbors = _vectorIndex.NearestNeighbors(queryVector, neighborK);

        var proposals = new List<(Handle Edge, float Sim, double Success, int Support)>();
        foreach (var (goalHandle, squaredDist) in neighbors)
        {
            // Squared Euclidean on L2-normalized vectors: ||a-b||² = 2 - 2·cos(a,b).
            // Clamp to [0,4] then convert to cosine similarity in [-1,1].
            float sim = Math.Clamp(1f - squaredDist / 2f, -1f, 1f);

            // Quality gate #2: skip goals that aren't genuinely similar. Below MinSimilarity the
            // goals are structurally different; proposing their tactics is noise.
            if (sim < MinSimilarity)
                continue;

            // Walk every tactic-edge that fires on this goal (BeforeRole incidence).
            foreach (var inc in _kernel.GetVertexHyperedges(goalHandle))
            {
                // The hyperedge handle is the edge-vertex. Read its tactic + stats.
                if (!_kernel.TryGetProperty(_tacticIdProp, inc, out var tacticId) ||
                    string.IsNullOrWhiteSpace(tacticId))
                    continue;
                if (!_kernel.TryGetProperty(_statsProp, inc, out var stats))
                    stats = EdgeStatistics.Initial;

                // Quality gate #3: skip tactics with no real track record. A freshly-seeded edge
                // starts at SuccessRate=1.0 (one observation) but that's a thin prior; require it
                // to have held up. Edges that failed once already drop below 0.5 and are excluded.
                if (stats.SuccessRate < MinSuccessRate)
                    continue;

                proposals.Add((inc, sim, stats.SuccessRate, stats.TransitionCount));
            }
        }

        // Rank-mix: same formula as Neo4jClient. Order by rank desc, take topK.
        var ranked = proposals
            .Select(p => new
            {
                p.Edge,
                p.Sim,
                Success = (float)p.Success,
                Support = p.Support,
                Rank = SimWeight * p.Sim
                     + SuccessWeight * (float)p.Success
                     + SupportWeight * MathF.Min(1f, MathF.Log10(Math.Max(1f, p.Support) + 1f)),
            })
            .OrderByDescending(x => x.Rank)
            .Take(topK)
            .ToList();

        var result = new List<GraphTacticProposal>(ranked.Count);
        foreach (var x in ranked)
        {
            _kernel.TryGetProperty(_tacticTextProp, x.Edge, out var tacticText);
            result.Add(new GraphTacticProposal
            {
                TacticId = _kernel.TryGetProperty(_tacticIdProp, x.Edge, out var tid) ? tid : "",
                TacticText = tacticText ?? "",
                NearestGoalSimilarity = x.Sim,
                HistoricalSuccessRate = x.Success,
                SupportCount = x.Support,
                RankScore = x.Rank,
            });
        }

        if (result.Count > 0)
        {
            _log.LogDebug(
                "ToposTacticStore.Propose: {N} goals, {C} candidate edges → top {K} (best sim={Sim:F3}, succ={Succ:F2})",
                neighbors.Count, proposals.Count, result.Count, result[0].NearestGoalSimilarity, result[0].HistoricalSuccessRate);
        }

        return Task.FromResult<IReadOnlyList<GraphTacticProposal>>(result);
    }

    public Task RecordOutcomeAsync(string tacticId, string goalText, bool succeeded, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(tacticId) || string.IsNullOrEmpty(goalText))
            return Task.CompletedTask;

        var goalHash = HashGoalText(goalText);
        lock (_writeLock)
        {
            if (!_edgeByGoalTactic.TryGetValue((goalHash, tacticId), out var edgeHandle))
                return Task.CompletedTask;  // edge not in store (e.g. proposed elsewhere)

            // Read-modify-write over the immutable EdgeStatistics value (the blessed pattern).
            var current = _kernel.TryGetProperty(_statsProp, edgeHandle, out var s)
                ? s : EdgeStatistics.Initial;
            var updated = current.Observe(succeeded);
            _kernel.SetProperty(_statsProp, edgeHandle, updated);
        }
        return Task.CompletedTask;
    }

    public Task RecordSuccessAsync(string goalText, float[] goalVector, string tacticText, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(goalText) || string.IsNullOrEmpty(tacticText)
            || goalVector is null || goalVector.Length == 0)
            return Task.CompletedTask;

        var goalHash = HashGoalText(goalText);
        var tacticId = ComputeHash(tacticText)[..16];

        lock (_writeLock)
        {
            var goalHandle = GetOrAddGoalVertex(goalHash, goalText, goalVector);
            var edgeKey = (goalHash, tacticId);
            if (!_edgeByGoalTactic.TryGetValue(edgeKey, out var edgeHandle))
            {
                edgeHandle = _kernel.CreateVertex(VertexRoles.Edge);
                _kernel.AddIncidence(edgeHandle, goalHandle, BeforeRole, ordinal: 0);
                _kernel.SetProperty(_tacticIdProp, edgeHandle, tacticId);
                _kernel.SetProperty(_tacticTextProp, edgeHandle, tacticText);
                _kernel.SetProperty(_statsProp, edgeHandle, EdgeStatistics.Initial);
                _edgeByGoalTactic[edgeKey] = edgeHandle;
            }
            // Reinforce (success). Read-modify-write over the immutable EdgeStatistics.
            var current = _kernel.TryGetProperty(_statsProp, edgeHandle, out var s)
                ? s : EdgeStatistics.Initial;
            _kernel.SetProperty(_statsProp, edgeHandle, current.Observe(succeeded: true));
        }
        return Task.CompletedTask;
    }

    public Task SeedFromFossilsAsync(IEnumerable<ProofFossil> fossils, CancellationToken ct)
    {
        int seeded = 0;
        foreach (var fossil in fossils)
        {
            ct.ThrowIfCancellationRequested();
            if (fossil.StateVector is null || fossil.StateVector.Length == 0)
                continue;

            lock (_writeLock)
            {
                // Goal vertex (dedup by the fossil's subgoal text hash — we don't have the
                // original goal hash, so hash the snippet the same way ProofGoalGraph does).
                var goalHash = HashGoalText(fossil.SubgoalText);
                var goalHandle = GetOrAddGoalVertex(goalHash, fossil.SubgoalText, fossil.StateVector);

                // Tactic edge — dedup by (goalHash, tacticId). Use the tactic block's own hash
                // as tacticId (fossils don't carry an external id; the block text is the identity).
                var tacticId = ComputeHash(fossil.TacticBlock)[..16];
                var edgeKey = (goalHash, tacticId);
                if (!_edgeByGoalTactic.TryGetValue(edgeKey, out var edgeHandle))
                {
                    edgeHandle = _kernel.CreateVertex(VertexRoles.Edge);
                    _kernel.AddIncidence(edgeHandle, goalHandle, BeforeRole, ordinal: 0);
                    _kernel.SetProperty(_tacticIdProp, edgeHandle, tacticId);
                    _kernel.SetProperty(_tacticTextProp, edgeHandle, fossil.TacticBlock);
                    // Seed stats: the fossil exists because this tactic once reduced sorry,
                    // so start it at success=1, count=1, confidence high.
                    _kernel.SetProperty(_statsProp, edgeHandle,
                        new EdgeStatistics(TransitionCount: 1, SuccessRate: 1.0, Confidence: 0.8));
                    _edgeByGoalTactic[edgeKey] = edgeHandle;
                    seeded++;
                }
            }
        }
        _log.LogInformation(
            "ToposTacticStore seeded from {Seeded} fossils ({Goals} goals, {Edges} tactic edges total)",
            seeded, GoalCount, TacticEdgeCount);
        return Task.CompletedTask;
    }

    private Handle GetOrAddGoalVertex(string goalHash, string goalText, float[] stateVector)
    {
        // Caller holds _writeLock.
        if (!_goalVertexByHash.TryGetValue(goalHash, out var handle))
        {
            handle = _kernel.CreateVertex();
            _kernel.SetProperty(_goalHashProp, handle, goalHash);
            _kernel.SetProperty(_goalTextProp, handle, goalText);
            _kernel.SetProperty(_stateVectorProp, handle, stateVector);
            _goalVertexByHash[goalHash] = handle;
        }
        return handle;
    }

    /// <summary>Same whitespace-normalization + SHA-256 hex hash ProofGoalGraph uses, so goal
    /// IDs are consistent if a future change threads the per-episode graph's IDs in here.</summary>
    private static string HashGoalText(string goalText)
    {
        var normalized = Agent.SketchValidator.NormalizeWhitespace(goalText ?? "");
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string ComputeHash(string input)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
