using RichLearning.V2.Abstractions;
using RichLearning.V2.Memory;
using RichLearning.V2.Models;

namespace NexusAgent.ToposExperiment.Search;

/// <summary>
/// AND-OR backward chainer over an <see cref="IGraphMemory"/> loaded with V2 HyperEdges.
///
/// <b>Verbatim copy of <c>NexusAgent.RlbExperiment.Search.NexusBackwardChainer</c></b> — the
/// search logic is identical, byte-for-byte. The ONLY change is the dependency type:
/// <c>InMemoryGraphMemory</c> → <see cref="IGraphMemory"/> on the constructor parameter and the
/// backing field. This is the ~6-site loosening the plan calls out: <c>InMemoryGraphMemory</c> is
/// <c>sealed</c>, so a Topos backend can't subclass it; the interface is the only seam.
///
/// Design (from the V2 original, preserved here):
///   - OR-branching: at each goal, try candidate HyperEdges in arm-specific order.
///   - AND-branching: chosen HyperEdge's Condition members must ALL be proved recursively.
///   - A goal with no outgoing HyperEdges is a leaf (trivially solved).
///   - Cycle guard: per-DFS-path HashSet prevents revisiting the same goal on the current branch.
///   - Fuel: integer step budget; each expansion (not each leaf visit) consumes one unit.
///
/// Trajectory recording (L11 tree-ancestry): each expansion node is appended to the TrajectoryDag
/// with parentId = the node that expanded the parent goal. The chainer does NOT call
/// BackwardReinforce — that is deferred to TrainingLoop post-search.
/// </summary>
internal sealed class NexusBackwardChainer
{
    private readonly IGraphMemory _memory;
    private readonly IReadOnlyDictionary<string, float[]> _vectors;

    public NexusBackwardChainer(
        IGraphMemory memory,
        IReadOnlyDictionary<string, float[]> vectors)
    {
        _memory = memory;
        _vectors = vectors;
    }

    public async Task<ProofSearchResult> SearchAsync(
        string rootGoalHash,
        float[] rootVector,
        SearchArm arm,
        int fuel,
        TrajectoryDag dag,
        CancellationToken ct = default)
    {
        var pathVisited = new HashSet<string>(StringComparer.Ordinal);
        return await SearchRecursiveAsync(
            rootGoalHash, rootVector, arm, fuel, dag,
            parentNodeId: null, pathVisited, depth: 0, ct);
    }

    private async Task<ProofSearchResult> SearchRecursiveAsync(
        string goalHash,
        float[] rootVector,
        SearchArm arm,
        int fuel,
        TrajectoryDag dag,
        string? parentNodeId,
        HashSet<string> pathVisited,
        int depth,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (pathVisited.Contains(goalHash))
            return ProofSearch.DeadEnd(parentNodeId); // cycle on this DFS path

        // A goal with no outgoing hyperedges is a proved leaf.
        var candidates = await _memory.GetHyperedgesByMemberAsync(
            new StateKey(goalHash), HyperEdgeRole.Anchor);

        if (candidates.Count == 0)
        {
            // Leaf: record a terminal node and report success.
            var (leafNode, _) = dag.Append(
                new StateKey(goalHash),
                new ActionKey("meta", "leaf-closed"),
                depth, 0.0, parentNodeId);
            return ProofSearch.Leaf(leafNode.Id);
        }

        if (fuel <= 0)
        {
            var (timeoutNode, _) = dag.Append(
                new StateKey(goalHash),
                new ActionKey("meta", "timeout"),
                depth, 0.0, parentNodeId);
            return ProofSearch.Timeout(timeoutNode.Id);
        }

        pathVisited.Add(goalHash);

        // Compute decision-time context (M2.2): captured before BackwardReinforce.
        float[] currentVec = _vectors.TryGetValue(goalHash, out var v) ? v : [];
        double cosineSim = ComputeCosineSim(currentVec, rootVector);
        var ctx = TraversalContext.Build(depth, cosineSim);

        // Order candidates by arm strategy (D1: v is ordering heuristic, not gate).
        var ordered = OrderCandidates(candidates, arm, ctx);

        string? lastNodeId = parentNodeId;

        foreach (var edge in ordered)
        {
            ct.ThrowIfCancellationRequested();

            // Record expansion node with tree-ancestry parentId (L11).
            var (expNode, isLoop) = dag.Append(
                new StateKey(goalHash),
                edge.SemanticAction,
                depth, 0.0,
                parentNodeId);

            expNode.FiredHyperedgeId = edge.Id;
            // M2.2: DecisionContext.set is internal to RichLearning.V2; store context in
            // Metadata so TrainingLoop can reconstruct it from the same captured values.
            expNode.Metadata = new Dictionary<string, object>
            {
                ["ctx_x0"] = ctx.X0,
                ["ctx_x1"] = ctx.X1,
            };

            lastNodeId = expNode.Id;

            if (isLoop)
            {
                // DAG detected this state was already visited somewhere in the episode.
                continue;
            }

            var conditions = edge.Conditions.ToList();

            if (conditions.Count == 0)
            {
                // Tactic closes the goal with no further subgoals.
                pathVisited.Remove(goalHash);
                return ProofSearch.Success(1, expNode.Id);
            }

            // Try to prove all AND-subgoals (L11 tree-ancestry: parent = expNode).
            bool allSolved = true;
            int stepsUsed  = 1; // this expansion counts as one step
            string? lastSubTerminal = expNode.Id;

            foreach (var condition in conditions)
            {
                var subResult = await SearchRecursiveAsync(
                    condition.Key.StableIdentity,
                    rootVector, arm,
                    fuel - stepsUsed,
                    dag,
                    parentNodeId: expNode.Id, // tree ancestry
                    pathVisited, depth + 1, ct);

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
                return ProofSearch.Success(stepsUsed, lastSubTerminal ?? expNode.Id);
            }
            // Otherwise try the next candidate edge.
        }

        pathVisited.Remove(goalHash);
        return ProofSearch.DeadEnd(lastNodeId);
    }

    private IEnumerable<HyperEdge> OrderCandidates(
        IReadOnlyList<HyperEdge> candidates,
        SearchArm arm,
        TraversalContext ctx) // pass by value so lambdas can capture it
    {
        return arm switch
        {
            SearchArm.BaselineN =>
                candidates.OrderBy(e => e.Id.StableIdentity, StringComparer.Ordinal),

            SearchArm.BaselineH =>
                candidates
                    .OrderByDescending(e => e.SuccessRate)
                    .ThenByDescending(e => e.TransitionCount)
                    .ThenBy(e => e.Id.StableIdentity, StringComparer.Ordinal),

            SearchArm.Learned =>
                candidates
                    .OrderByDescending(e => e.Evaluate(ctx)) // D1: v is ordering score
                    .ThenBy(e => e.Id.StableIdentity, StringComparer.Ordinal),

            _ => candidates,
        };
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
}
