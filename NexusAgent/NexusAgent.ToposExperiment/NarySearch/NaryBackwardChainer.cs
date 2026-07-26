using NexusAgent.ToposExperiment.Ingest;
using RichLearning.V2.Abstractions;
using RichLearning.V2.Memory;
using RichLearning.V2.Models;
using Topos.Hypergraph;

namespace NexusAgent.ToposExperiment.NarySearch;

/// <summary>
/// AND-OR backward chainer that reads the Topos hypergraph <b>natively</b> — no V2
/// <c>HyperEdge</c> projection, no <c>IGraphMemory</c>. This is the "deferred n-ary chainer
/// rewrite" the faithful-port milestone deliberately left for later: where the projected chainer
/// stored n-ary structure in Topos but re-imposed V2's Anchor/Target shape at the boundary, this
/// chainer consumes Topos's genuine n-ary shape directly.
///
/// <b>What "native n-ary reading" means concretely:</b> given a goal (by hash), resolve to its
/// Topos vertex Handle, then <see cref="IHypergraphQuery.GetVertexHyperedges"/> returns every
/// edge-vertex that goal participates in. For each candidate edge-vertex,
/// <see cref="HypergraphKernel.IncidencesFrom"/> returns its member incidences; filter by
/// <see cref="ToposAppliesAdapter.BeforeRole"/> (the goal being applied to) vs
/// <see cref="ToposAppliesAdapter.AfterRole"/> (the subgoals produced). The after-role members
/// are the AND-branch — the genuine n-ary fact, as many as the tactic produced, no contortion to
/// fit an exactly-one-Target slot.
///
/// <b>What this eliminates vs the projected chainer:</b>
///   - No V2 <c>HyperEdge</c> allocation per query (the projection cache + reference-stability
///     contract from <c>ToposGraphMemory</c> — issue #2 in the integration report — simply
///     doesn't exist here. The kernel is the single source of truth; "reference stability" is
///     automatic because there's one kernel object.).
///   - No "L12 uniform mapping" (stuffing the Anchor key into the Target slot to satisfy V2
///     cardinality). A tactic producing N subgoals is read as N after-role members, full stop.
///   - Candidate ordering reads the edge-vertex's properties through the kernel's typed pools
///     (<c>EdgeStatistics</c>, <c>LearnableEdge</c>) — Topos-native learning state, not V2's.
///
/// <b>What stays the same:</b> the search algorithm (OR-branch across candidates, AND-branch
/// across an edge's subgoals, per-DFS-path cycle guard, integer fuel budget). The trajectory
/// machinery (V2 <c>TrajectoryDag</c>/<c>TrajectoryNode</c>) is reused as-is — it's a generic
/// Merkle-linked causal-chain structure, domain-agnostic by design, and the chainer's job is to
/// populate it, not own it. What the trajectory records about each step is what changes:
/// <c>FiredHyperedgeId</c> carries the Topos edge-vertex's identity encoded as a
/// <see cref="StateKey"/>, which <c>ToposNativeCreditAssignment</c> resolves back to a Handle.
/// </summary>
internal sealed class NaryBackwardChainer
{
    private readonly HypergraphKernel _kernel;
    private readonly IReadOnlyDictionary<string, Handle> _goalHandleByHash;
    private readonly IReadOnlyDictionary<string, float[]> _vectors;

    // Edge-vertex properties (resolved once; the kernel's property registry dedups by name).
    private readonly PropertyKey<LearnableEdge> _learnable;
    private readonly PropertyKey<EdgeStatistics> _stats;
    private readonly PropertyKey<string> _tacticIdProp;

    public NaryBackwardChainer(
        HypergraphKernel kernel,
        IReadOnlyDictionary<string, Handle> goalHandleByHash,
        IReadOnlyDictionary<string, float[]> vectors)
    {
        _kernel = kernel;
        _goalHandleByHash = goalHandleByHash;
        _vectors = vectors;
        _learnable = kernel.ResolveProperty<LearnableEdge>("learnable");
        _stats = kernel.ResolveProperty<EdgeStatistics>("stats");
        _tacticIdProp = kernel.ResolveProperty<string>("tacticId");
    }

    public async Task<NaryProofSearchResult> SearchAsync(
        string rootGoalHash,
        float[] rootVector,
        NarySearchArm arm,
        int fuel,
        TrajectoryDag dag,
        CancellationToken ct = default)
    {
        var pathVisited = new HashSet<string>(StringComparer.Ordinal);
        return await SearchRecursiveAsync(
            rootGoalHash, rootVector, arm, fuel, dag,
            parentNodeId: null, pathVisited, depth: 0, ct);
    }

    private async Task<NaryProofSearchResult> SearchRecursiveAsync(
        string goalHash,
        float[] rootVector,
        NarySearchArm arm,
        int fuel,
        TrajectoryDag dag,
        string? parentNodeId,
        HashSet<string> pathVisited,
        int depth,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (pathVisited.Contains(goalHash))
            return NaryProofSearch.DeadEnd(parentNodeId);

        // Resolve goal-hash → Topos Handle. A goal that was never ingested has no candidates and
        // is treated as a proved leaf (same convention as the projected chainer).
        if (!_goalHandleByHash.TryGetValue(goalHash, out var goalHandle))
        {
            var (leafNode, _) = dag.Append(
                new StateKey(goalHash), new ActionKey("meta", "leaf-closed"),
                depth, 0.0, parentNodeId);
            return NaryProofSearch.Leaf(leafNode.Id);
        }

        // Native n-ary read: every edge-vertex this goal participates in. These are the candidate
        // tactic-applications — the OR-branch set. GetVertexHyperedges returns the distinct
        // Source handles from IncidencesOf(goalHandle), i.e. every edge that has this goal as a
        // member. We then keep only those where the goal is the *before* member (BeforeRole) —
        // the same semantic as V2's HyperEdgeRole.Anchor filter.
        var candidateEdges = CandidateEdgesFor(goalHandle);

        if (candidateEdges.Count == 0)
        {
            var (leafNode, _) = dag.Append(
                new StateKey(goalHash), new ActionKey("meta", "leaf-closed"),
                depth, 0.0, parentNodeId);
            return NaryProofSearch.Leaf(leafNode.Id);
        }

        if (fuel <= 0)
        {
            var (timeoutNode, _) = dag.Append(
                new StateKey(goalHash), new ActionKey("meta", "timeout"),
                depth, 0.0, parentNodeId);
            return NaryProofSearch.Timeout(timeoutNode.Id);
        }

        pathVisited.Add(goalHash);

        // Decision-time context, captured before any BackwardReinforce. Same two features as the
        // projected path's TraversalContext: X0 = tanh(depth/10), X1 = cosine-sim(current, root).
        float[] currentVec = _vectors.TryGetValue(goalHash, out var v) ? v : [];
        double cosineSim = ComputeCosineSim(currentVec, rootVector);
        var ctx = TraversalContext.Build(depth, cosineSim);

        var ordered = OrderCandidates(candidateEdges, arm, ctx);

        string? lastNodeId = parentNodeId;

        foreach (var edgeHandle in ordered)
        {
            ct.ThrowIfCancellationRequested();

            _kernel.TryGetProperty(_tacticIdProp, edgeHandle, out var tacticId);
            var actionKey = new ActionKey("tactic", tacticId ?? "unknown");

            var (expNode, isLoop) = dag.Append(
                new StateKey(goalHash), actionKey, depth, 0.0, parentNodeId);

            // Record which Topos edge-vertex fired, encoded as a StateKey. Credit assignment
            // resolves this back to the Handle via the Handle-keyed index.
            expNode.FiredHyperedgeId = EdgeIdentity.Of(edgeHandle);
            expNode.Metadata = new Dictionary<string, object>
            {
                ["ctx_x0"] = ctx.X0,
                ["ctx_x1"] = ctx.X1,
            };

            lastNodeId = expNode.Id;

            if (isLoop) continue;

            // Native n-ary AND-branch: the edge's after-role members are the subgoals, all of
            // which must be proved. No Target slot, no cardinality contortion — read as many
            // subgoals as the tactic actually produced.
            var subgoalHashes = SubgoalsOf(edgeHandle);

            if (subgoalHashes.Count == 0)
            {
                // Tactic closes the goal with no subgoals (a leaf-producing tactic).
                pathVisited.Remove(goalHash);
                return NaryProofSearch.Success(1, expNode.Id);
            }

            bool allSolved = true;
            int stepsUsed = 1;
            string? lastSubTerminal = expNode.Id;

            foreach (var subHash in subgoalHashes)
            {
                var subResult = await SearchRecursiveAsync(
                    subHash, rootVector, arm, fuel - stepsUsed, dag,
                    parentNodeId: expNode.Id, pathVisited, depth + 1, ct);

                stepsUsed += subResult.Steps;
                lastSubTerminal = subResult.TerminalNodeId ?? lastSubTerminal;

                if (!subResult.IsSuccess)
                {
                    allSolved = false;
                    break;
                }
            }

            if (allSolved)
            {
                pathVisited.Remove(goalHash);
                return NaryProofSearch.Success(stepsUsed, lastSubTerminal ?? expNode.Id);
            }
        }

        pathVisited.Remove(goalHash);
        return NaryProofSearch.DeadEnd(lastNodeId);
    }

    /// <summary>
    /// The OR-branch candidate set: every edge-vertex in which <paramref name="goalHandle"/>
    /// participates as a <see cref="ToposAppliesAdapter.BeforeRole"/> member. Equivalent to the
    /// projected chainer's GetHyperedgesByMemberAsync(anchor, HyperEdgeRole.Anchor), but read
    /// straight off the incidence index with no projection in between.
    /// </summary>
    private List<Handle> CandidateEdgesFor(Handle goalHandle)
    {
        var result = new List<Handle>();
        foreach (var edge in _kernel.GetVertexHyperedges(goalHandle))
        {
            // Keep only edges where this goal is the *before* member (not an after-subgoal of
            // some other tactic). IncidencesOf returns every incidence touching goalHandle; we
            // need the one whose Source is `edge` and check its Role.
            foreach (var inc in _kernel.IncidencesOf(goalHandle))
            {
                if (inc.Source == edge && inc.Role == ToposAppliesAdapter.BeforeRole)
                {
                    result.Add(edge);
                    break;
                }
            }
        }
        return result;
    }

    /// <summary>
    /// The AND-branch: the after-role members of <paramref name="edgeHandle"/>, returned as
    /// goal-hash strings (so the recursion can re-resolve them to Handles). Ordered by Ordinal
    /// for determinism. This is the genuine n-ary read — as many subgoals as the tactic produced.
    /// </summary>
    private List<string> SubgoalsOf(Handle edgeHandle)
    {
        var result = new List<string>();
        var beforeHashProp = _kernel.ResolveProperty<string>("beforeHash");
        var afterHashesProp = _kernel.ResolveProperty<string[]>("afterHashes");

        // The adapter stored the after-hashes as a property on the edge-vertex at ingest time
        // (deterministic, ordered). Reading that avoids a Handle→hash reverse lookup per member.
        if (_kernel.TryGetProperty(afterHashesProp, edgeHandle, out var afterHashes))
            return afterHashes.ToList();

        // Fallback (shouldn't happen with TogosAppliesAdapter, but defensive): reconstruct from
        // the incidences by reverse-resolving member Handles to hashes.
        foreach (var inc in _kernel.IncidencesFrom(edgeHandle).OrderBy(i => i.Ordinal))
        {
            if (inc.Role != ToposAppliesAdapter.AfterRole) continue;
            // We don't have a reverse map here; the property path above is the supported one.
            // If we ever reach here it's a real bug — surface it.
            throw new InvalidOperationException(
                "afterHashes property missing on edge-vertex; the adapter must set it at ingest. " +
                "This indicates TogosAppliesAdapter and NaryBackwardChainer are out of sync.");
        }
        return result;
    }

    /// <summary>
    /// Candidate ordering by arm. Reads the edge-vertex's properties through the kernel's typed
    /// pools — Topos-native learning state, not V2's. The LearnableEdge arm computes the same
    /// two-feature vector (X0/X1) the projected path used, so a fair before/after comparison is
    /// possible (the projection's HyperEdge.Evaluate(ctx) and this LearnableEdge.Evaluate(features)
    /// are the same math over the same features — see TOPOS_INTEGRATION_REPORT.md).
    /// </summary>
    private IEnumerable<Handle> OrderCandidates(
        IReadOnlyList<Handle> candidates, NarySearchArm arm, TraversalContext ctx)
    {
        return arm switch
        {
            NarySearchArm.BaselineN =>
                candidates.OrderBy(h => h.Index), // ingest order

            NarySearchArm.BaselineH =>
                candidates
                    .Select(h => (Handle: h, Stats: TryGetStats(h)))
                    .OrderByDescending(x => x.Stats.SuccessRate)
                    .ThenByDescending(x => x.Stats.TransitionCount)
                    .ThenBy(x => x.Handle.Index)
                    .Select(x => x.Handle),

            NarySearchArm.Learned =>
                candidates
                    .Select(h => (Handle: h, Score: EvaluateLearned(h, ctx)))
                    .OrderByDescending(x => x.Score)
                    .ThenBy(x => x.Handle.Index)
                    .Select(x => x.Handle),

            _ => candidates,
        };
    }

    private EdgeStatistics TryGetStats(Handle h) =>
        _kernel.TryGetProperty(_stats, h, out var s) ? s : EdgeStatistics.Initial;

    private float EvaluateLearned(Handle h, TraversalContext ctx)
    {
        if (!_kernel.TryGetProperty(_learnable, h, out var le))
            return 0.5f; // uninitialized → neutral, matching V2's convention

        // Two features matching V2's EdgeFeatureLayout decision-context slots (X0, X1). Note V2's
        // HyperEdge.Evaluate *also* mixes in SuccessRate/Confidence/TransitionCount from r[3..];
        // this n-ary path uses a pure 2-feature LearnableEdge (theta length 3: bias + 2). That's
        // a deliberate simplification — the goal is to prove the n-ary *storage and traversal*
        // work, not to reproduce V2's exact 7-slot theta. See report §"feature-vector parity".
        ReadOnlySpan<float> features = [(float)ctx.X0, (float)ctx.X1];
        return le.Evaluate(features);
    }

    private static double ComputeCosineSim(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length == 0 || a.Length != b.Length)
            return 0.0;

        double dot = 0.0, normA = 0.0, normB = 0.0;
        for (int i = 0; i < a.Length; i++)
        {
            dot   += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        double denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom < 1e-10 ? 0.0 : dot / denom;
    }

    /// <summary>Encodes/decodes a Topos edge-vertex Handle as the trajectory's StateKey identity.</summary>
    public static class EdgeIdentity
    {
        private const string Prefix = "topos-edge:";

        public static StateKey Of(Handle h) => new($"{Prefix}{h.Index}");

        public static bool TryGetHandle(StateKey id, out Handle handle)
        {
            handle = default;
            var s = id.StableIdentity;
            if (!s.StartsWith(Prefix, StringComparison.Ordinal)) return false;
            return uint.TryParse(s.AsSpan(Prefix.Length), out var idx);
        }
    }
}
