using NexusAgent.ToposExperiment.Memory;
using NexusAgent.ToposExperiment.Models;
using RichLearning.V2.Abstractions;
using RichLearning.V2.Models;
using Topos.Hypergraph;

namespace NexusAgent.ToposExperiment.Ingest;

/// <summary>
/// Converts raw <see cref="AppliesEdge"/> records into a Topos <see cref="HypergraphKernel"/>
/// (genuine n-ary storage) and projects each tactic-application to a V2 <see cref="HyperEdge"/>
/// cached on the <see cref="ToposGraphMemory"/> for the chainer to consume.
///
/// <b>Mirrors <c>NexusAgent.RlbExperiment.Ingest.AppliesHyperedgeAdapter</c> in shape</b> — same
/// input type (<see cref="AppliesEdge"/>), same grouping rule (group by
/// (GoalBeforeHash, TacticId) → one hyperedge), same statistics aggregation. The difference is
/// internal: where the V2 adapter builds V2 <see cref="HyperEdge"/> objects directly in an
/// <c>InMemoryGraphMemory</c>, this adapter stores the *genuine* n-ary structure natively in a
/// Topos kernel and then *projects* to the V2 shape at the boundary.
///
/// <b>The n-ary shape in Topos (the structurally-honest representation):</b>
///   one <c>VertexRoles.Edge</c> vertex per (GoalBeforeHash, TacticId) group
///   → <c>AddIncidence(edge, beforeGoal, BeforeRole=0, ordinal 0)</c>
///   → <c>AddIncidence(edge, afterGoal_i, AfterRole=1, ordinal i+1)</c> for each distinct subgoal
///
/// This is one Edge-vertex with N+1 member incidences — exactly the "one tactic produces N
/// subgoals" atomic fact, with no contortion. Compare to the V2 adapter's "L12 uniform mapping",
/// which stuffs the Anchor key back into the Target slot to satisfy V2's exactly-one-Target
/// cardinality (see <c>HyperEdge.ValidateCardinality</c>); that workaround is listed as carry-back
/// CB1 in <c>docs/RLB_V2_FUTURE_WORK.md</c> and is the precise structural limitation Topos doesn't
/// have. <b>However</b> — since the chainer consumes V2 shape at the boundary, this adapter still
/// projects to that shape (Anchor=before, Conditions=afters, Target=before) for chainer parity.
/// The Topos-side n-ary storage is the thing under test; the boundary projection is the
/// compatibility layer. The deferred "n-ary chainer" scope (see plan) would consume the native
/// Topos shape directly and drop the projection.
///
/// <b>Role bytes are adapter-owned, per Topos's design</b> ("the kernel records; it does not
/// judge" — <c>src/Topos.Hypergraph/Incidence.cs</c>). The mapping:
///   <see cref="BeforeRole"/> = 0  (the goal-state before the tactic fires — V2 "Anchor")
///   <see cref="AfterRole"/>  = 1  (each subgoal produced — V2 "Condition")
/// This mirrors the <c>samples/Topos.Samples.ChatMemory/ChatMemory.cs</c> pattern of
/// adapter-owned <c>const byte</c> roles.
/// </summary>
internal static class ToposAppliesAdapter
{
    /// <summary>V2 Anchor equivalent: the goal-state the tactic is applied to.</summary>
    public const byte BeforeRole = 0;

    /// <summary>V2 Condition equivalent: each subgoal the tactic produces.</summary>
    public const byte AfterRole = 1;

    public static async Task<ToposGraphMemory> BuildMemoryAsync(
        List<AppliesEdge> edges,
        CancellationToken ct = default)
    {
        var memory = new ToposGraphMemory();
        await memory.InitialiseSchemaAsync();
        var kernel = memory.Kernel;

        // Per-adapter typed properties on the *edge-vertex* (the ChatMemory.RecordRecallFeedback
        // pattern). Topos's documented per-Incidence cell-property mechanism (Incidence.cs:12-17)
        // promises per-(source,member,ordinal) addressing "lands with M2's reification work", but
        // M2 shipped nested reification only — per-incidence addressing isn't built. Per-edge-vertex
        // is the public-API-verified workaround (issue #1 in TOPOS_INTEGRATION_REPORT.md). The
        // chainer's credit assignment reads these off the projected HyperEdge, so they must
        // round-trip through the projection below.
        var transitionCountProp = kernel.ResolveProperty<int>("transitionCount");
        var successRateProp     = kernel.ResolveProperty<double>("successRate");
        var confidenceProp      = kernel.ResolveProperty<double>("confidence");
        var tacticIdProp        = kernel.ResolveProperty<string>("tacticId");
        var beforeHashProp      = kernel.ResolveProperty<string>("beforeHash");
        var afterHashesProp     = kernel.ResolveProperty<string[]>("afterHashes");

        // Dedup goal-hashes across the whole graph → one Topos vertex per distinct goal. This is
        // how real junctions surface in Topos: the same goal-hash vertex participates in multiple
        // tactic-edge incidences, and GetVertexHyperedges(goal) returns all of them. (FC100's
        // 0.3% junction density is a property of that corpus's data, not of the storage model —
        // Topos represents whatever junctions the data has, natively.)
        var goalVertexByHash = new Dictionary<string, Handle>(StringComparer.Ordinal);

        Handle GoalVertexFor(string hash)
        {
            if (!goalVertexByHash.TryGetValue(hash, out var h))
            {
                h = kernel.CreateVertex();
                goalVertexByHash[hash] = h;
            }
            return h;
        }

        // Group by (GoalBeforeHash, TacticId) → one hyperedge per group. Identical rule to the V2
        // adapter's L12 mapping.
        var groups = edges
            .GroupBy(e => (e.GoalBeforeHash, e.TacticId), StringTupleComparer.Instance)
            .ToList();

        Console.WriteLine($"[ToposAppliesAdapter] building {groups.Count:N0} tactic-hyperedges " +
                          $"(native n-ary in Topos) from {edges.Count:N0} APPLIES edges…");

        int idx = 0;
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();

            var (goalBefore, tacticId) = group.Key;
            var afterHashes = group
                .Select(e => e.GoalAfterHash)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(h => h, StringComparer.Ordinal) // deterministic member ordering — same as V2
                .ToList();

            int totalCount   = group.Sum(e => e.Count);
            int totalSuccess = group.Sum(e => e.SuccessSum);
            double successRate = totalCount > 0 ? (double)totalSuccess / totalCount : 0.0;
            double confidence  = Math.Tanh(totalCount / 100.0);

            // ── Native n-ary storage in Topos ──────────────────────────────────
            // One Edge-vertex per tactic-application; one incidence to the before-goal
            // (BeforeRole, ordinal 0); one incidence per after-subgoal (AfterRole, ordinal i+1).
            var edgeVertex = kernel.CreateVertex(VertexRoles.Edge);
            var beforeHandle = GoalVertexFor(goalBefore);
            kernel.AddIncidence(edgeVertex, beforeHandle, BeforeRole, ordinal: 0);

            var afterHandles = new List<Handle>(afterHashes.Count);
            for (int i = 0; i < afterHashes.Count; i++)
            {
                var afterHandle = GoalVertexFor(afterHashes[i]);
                kernel.AddIncidence(edgeVertex, afterHandle, AfterRole, ordinal: i + 1);
                afterHandles.Add(afterHandle);
            }

            // Edge-level properties (per-edge-vertex; the per-incidence gap is issue #1).
            kernel.SetProperty(transitionCountProp, edgeVertex, Math.Max(1, totalCount));
            kernel.SetProperty(successRateProp,     edgeVertex, successRate);
            kernel.SetProperty(confidenceProp,      edgeVertex, confidence);
            kernel.SetProperty(tacticIdProp,        edgeVertex, tacticId);
            kernel.SetProperty(beforeHashProp,      edgeVertex, goalBefore);
            kernel.SetProperty(afterHashesProp,     edgeVertex, afterHashes.ToArray());

            // ── Projection to V2 HyperEdge shape (the boundary the chainer consumes) ──
            // Same "L12 uniform mapping" as the V2 adapter: Anchor = beforeGoal, Conditions = afters,
            // Target = beforeGoal (V2 insists on exactly one Target; a tactic producing N subgoals
            // has no single "target", so the same key is reused). This is the workaround CB1 names;
            // the deferred n-ary chainer scope would consume Topos's native shape and drop this.
            var members = new List<HyperEdgeMember>(afterHashes.Count + 2)
            {
                new(new StateKey(goalBefore), HyperEdgeRole.Anchor, 0),
            };
            for (int i = 0; i < afterHashes.Count; i++)
                members.Add(new HyperEdgeMember(new StateKey(afterHashes[i]), HyperEdgeRole.Condition, i + 1));
            members.Add(new HyperEdgeMember(new StateKey(goalBefore), HyperEdgeRole.Target, afterHashes.Count + 1));

            var hyperEdge = new HyperEdge
            {
                Id = new StateKey($"he:{goalBefore}:{tacticId}"),
                SemanticAction = new ActionKey("tactic", tacticId),
                Members = members,
                TransitionCount = Math.Max(1, totalCount),
                SuccessRate = successRate,
                Confidence = confidence,
            };

            // Hand the exact instance to the memory — UpsertHyperedgeAsync caches it and returns
            // that same reference from reads, which is the contract CreditAssignment relies on.
            await memory.UpsertHyperedgeAsync(hyperEdge);

            // UpsertLandmarkAsync is a no-op in ToposGraphMemory but the V2 adapter calls it; we
            // mirror the calls for parity (the V2 adapter's comment explains these exist only to
            // satisfy InMemoryGraphMemory's internal index — Topos doesn't need them).
            await memory.UpsertLandmarkAsync(new StateLandmark { Id = new StateKey(goalBefore), Embedding = [] });
            foreach (var afterHash in afterHashes)
                await memory.UpsertLandmarkAsync(new StateLandmark { Id = new StateKey(afterHash), Embedding = [] });

            idx++;
            if (idx % 10_000 == 0)
                Console.WriteLine($"  … {idx:N0} / {groups.Count:N0}");
        }

        Console.WriteLine($"[ToposAppliesAdapter] done. {groups.Count:N0} hyperedges projected; " +
                          $"{kernel.CountVertices()} vertices / {kernel.AllIncidences().Count()} incidences in the Topos kernel.");
        return memory;
    }

    /// <summary>
    /// Builds the Topos kernel for the n-ary chainer path — no V2 projection, no
    /// <see cref="ToposGraphMemory"/>. Returns the kernel plus the goalHash→Handle map the n-ary
    /// chainer needs to resolve goal-hashes to vertices during search.
    ///
    /// The kernel-population logic is shared in spirit with <see cref="BuildMemoryAsync"/> (same
    /// grouping rule, same property names, same role bytes), but kept as a separate method rather
    /// than refactored to a shared helper. Reason: the projected path is already tested and frozen
    /// (it's the V2-parity baseline); extracting a shared core risks drifting the projected path's
    /// behavior, and the population logic is short and stable enough that the duplication cost is
    /// lower than the coupling cost. The two methods write the same property names
    /// (<c>tacticId</c>, <c>beforeHash</c>, <c>afterHashes</c>, <c>stats</c>) so the n-ary chainer
    /// and credit assignment can read either backend's kernel identically.
    /// </summary>
    public static Task<NaryKernelBuild> BuildNaryAsync(
        List<AppliesEdge> edges,
        CancellationToken ct = default)
    {
        var kernel = new HypergraphKernel();
        var goalHandleByHash = new Dictionary<string, Handle>(StringComparer.Ordinal);

        // Same property names as BuildMemoryAsync — the n-ary chainer/credit read these.
        var statsProp       = kernel.ResolveProperty<EdgeStatistics>("stats");
        var tacticIdProp    = kernel.ResolveProperty<string>("tacticId");
        var beforeHashProp  = kernel.ResolveProperty<string>("beforeHash");
        var afterHashesProp = kernel.ResolveProperty<string[]>("afterHashes");

        Handle GoalVertexFor(string hash)
        {
            if (!goalHandleByHash.TryGetValue(hash, out var h))
            {
                h = kernel.CreateVertex();
                goalHandleByHash[hash] = h;
            }
            return h;
        }

        var groups = edges
            .GroupBy(e => (e.GoalBeforeHash, e.TacticId), StringTupleComparer.Instance)
            .ToList();

        Console.WriteLine($"[ToposAppliesAdapter.BuildNary] building {groups.Count:N0} n-ary tactic-edges " +
                          $"from {edges.Count:N0} APPLIES edges (no V2 projection)…");

        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();

            var (goalBefore, tacticId) = group.Key;
            var afterHashes = group
                .Select(e => e.GoalAfterHash)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(h => h, StringComparer.Ordinal)
                .ToList();

            int totalCount   = group.Sum(e => e.Count);
            int totalSuccess = group.Sum(e => e.SuccessSum);
            double successRate = totalCount > 0 ? (double)totalSuccess / totalCount : 0.0;
            double confidence  = Math.Tanh(totalCount / 100.0);

            var edgeVertex = kernel.CreateVertex(VertexRoles.Edge);
            kernel.AddIncidence(edgeVertex, GoalVertexFor(goalBefore), BeforeRole, ordinal: 0);
            for (int i = 0; i < afterHashes.Count; i++)
                kernel.AddIncidence(edgeVertex, GoalVertexFor(afterHashes[i]), AfterRole, ordinal: i + 1);

            // The n-ary path uses EdgeStatistics (the LearnableEdge is created lazily by credit
            // assignment on first reinforcement, so we don't set it here — matches V2's
            // uninitialized-theta convention).
            kernel.SetProperty(statsProp, edgeVertex,
                new EdgeStatistics(Math.Max(1, totalCount), successRate, confidence));
            kernel.SetProperty(tacticIdProp,    edgeVertex, tacticId);
            kernel.SetProperty(beforeHashProp,  edgeVertex, goalBefore);
            kernel.SetProperty(afterHashesProp, edgeVertex, afterHashes.ToArray());
        }

        Console.WriteLine($"[ToposAppliesAdapter.BuildNary] done. {groups.Count:N0} tactic-edges; " +
                          $"{kernel.CountVertices()} vertices / {kernel.AllIncidences().Count()} incidences.");

        return Task.FromResult(new NaryKernelBuild(kernel, goalHandleByHash, groups.Count));
    }

    /// <summary>Result of <see cref="BuildNaryAsync"/>: the kernel + the resolution map the chainer needs.</summary>
    public sealed record NaryKernelBuild(
        HypergraphKernel Kernel,
        Dictionary<string, Handle> GoalHandleByHash,
        int EdgeCount);

    private sealed class StringTupleComparer : IEqualityComparer<(string, string)>
    {
        public static readonly StringTupleComparer Instance = new();

        public bool Equals((string, string) x, (string, string) y) =>
            string.Equals(x.Item1, y.Item1, StringComparison.Ordinal) &&
            string.Equals(x.Item2, y.Item2, StringComparison.Ordinal);

        public int GetHashCode((string, string) obj) =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.Item1),
                StringComparer.Ordinal.GetHashCode(obj.Item2));
    }
}
