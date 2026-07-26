using Neo4j.Driver;
using NexusAgent.Core.Configuration;
using NexusAgent.ToposExperiment.Models;

namespace NexusAgent.ToposExperiment.Ingest;

/// <summary>
/// Loads the full APPLIES graph from Neo4j into memory. Verbatim logic from
/// <c>NexusAgent.RlbExperiment.Ingest.RlbGraphLoader</c> (the V2 experiment's loader) — same
/// Cypher, same OF-1 verbatim-hash rule, same root-goal derivation. Only the namespace is changed
/// (and hence the <see cref="AppliesEdge"/>/<see cref="GoalEntry"/> types it produces, which are
/// verbatim copies in this project's <see cref="Models"/> namespace).
///
/// Reused rather than reimplemented because the loader is upstream of the storage backend: it
/// produces raw <see cref="AppliesEdge"/> records, and the Topos/V2 distinction is entirely in
/// how those records are then *stored* (ToposAppliesAdapter vs AppliesHyperedgeAdapter). A
/// backend-swap integration has no reason to touch this layer.
/// </summary>
public sealed class RlbGraphLoader : IAsyncDisposable
{
    private readonly IDriver _driver;
    private readonly string _database;

    public RlbGraphLoader(NexusConfig cfg)
    {
        _driver = GraphDatabase.Driver(cfg.Neo4jUri, AuthTokens.Basic(cfg.Neo4jUser, cfg.Neo4jPassword));
        _database = cfg.Neo4jDatabase;
    }

    /// <summary>
    /// Loads all APPLIES edges with stateVectors for both endpoint goals.
    /// Returns a list of raw edges and a stateVector dictionary keyed by goal hash.
    /// </summary>
    public async Task<(List<AppliesEdge> Edges, Dictionary<string, float[]> Vectors)> LoadGraphAsync(
        CancellationToken ct = default)
    {
        Console.WriteLine("[RlbGraphLoader] streaming APPLIES edges from Neo4j…");

        var edges = new List<AppliesEdge>(260_000);
        var vectors = new Dictionary<string, float[]>(270_000, StringComparer.Ordinal);

        await using var session = _driver.AsyncSession(o => o.WithDatabase(_database));
        var cursor = await session.RunAsync(
            """
            MATCH (g1:GoalShape)-[a:APPLIES]->(g2:GoalShape)
            RETURN g1.hash           AS beforeHash,
                   g2.hash           AS afterHash,
                   a.tactic_id       AS tacticId,
                   a.theorem_id      AS theoremId,
                   a.split           AS split,
                   coalesce(a.count, 1)       AS cnt,
                   coalesce(a.success_sum, 0) AS succSum,
                   g1.stateVector    AS beforeVec,
                   g2.stateVector    AS afterVec
            """);

        await foreach (var rec in cursor)
        {
            ct.ThrowIfCancellationRequested();

            var beforeHash = rec["beforeHash"].As<string>();
            var afterHash  = rec["afterHash"].As<string>();
            var tacticId   = rec["tacticId"].As<string>();
            var theoremId  = rec["theoremId"].As<string>();
            var split      = rec["split"].As<string>() ?? "train";
            var cnt        = rec["cnt"].As<int>();
            var succSum    = rec["succSum"].As<int>();

            float[] beforeVec = ParseVector(rec["beforeVec"]);
            float[] afterVec  = ParseVector(rec["afterVec"]);

            edges.Add(new AppliesEdge(
                beforeHash, afterHash, tacticId, theoremId, split,
                cnt, succSum, beforeVec, afterVec));

            if (beforeVec.Length > 0) vectors.TryAdd(beforeHash, beforeVec);
            if (afterVec.Length  > 0) vectors.TryAdd(afterHash,  afterVec);
        }

        Console.WriteLine($"[RlbGraphLoader] loaded {edges.Count:N0} APPLIES edges, {vectors.Count:N0} goal vectors");
        return (edges, vectors);
    }

    /// <summary>
    /// Computes root goals (per split) in memory after the graph is loaded.
    /// Root goal of a theorem = GoalBefore that never appears as GoalAfter for that theorem.
    /// Falls back to the first-seen GoalBefore per theorem when no root can be determined.
    /// </summary>
    public static (List<GoalEntry> TrainRoots, List<GoalEntry> EvalRoots) ComputeRootGoals(
        List<AppliesEdge> edges,
        Dictionary<string, float[]> vectors)
    {
        // Build per-theorem afterHash sets so we can find roots.
        var afterSets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var firstBefore = new Dictionary<string, AppliesEdge>(StringComparer.Ordinal); // theoremId → first seen edge

        foreach (var e in edges)
        {
            if (!afterSets.TryGetValue(e.TheoremId, out var aset))
            {
                aset = new HashSet<string>(StringComparer.Ordinal);
                afterSets[e.TheoremId] = aset;
            }
            aset.Add(e.GoalAfterHash);
            firstBefore.TryAdd(e.TheoremId, e);
        }

        // Collect candidates: per theorem, GoalBefore not in afterSet.
        var trainRootMap = new Dictionary<string, GoalEntry>(StringComparer.Ordinal);
        var evalRootMap  = new Dictionary<string, GoalEntry>(StringComparer.Ordinal);

        foreach (var e in edges)
        {
            if (!afterSets.TryGetValue(e.TheoremId, out var aset)) continue;
            if (aset.Contains(e.GoalBeforeHash)) continue; // not a root

            var entry = new GoalEntry(
                e.TheoremId,
                e.GoalBeforeHash,
                vectors.TryGetValue(e.GoalBeforeHash, out var v) ? v : []);

            if (string.Equals(e.Split, "eval", StringComparison.OrdinalIgnoreCase))
                evalRootMap.TryAdd(e.TheoremId, entry);
            else
                trainRootMap.TryAdd(e.TheoremId, entry);
        }

        // Fallback: theorems where every GoalBefore also appears as GoalAfter (rare cycles) → use first seen
        foreach (var (tid, fe) in firstBefore)
        {
            bool inTrain = string.Equals(fe.Split, "train", StringComparison.OrdinalIgnoreCase);
            var map = inTrain ? trainRootMap : evalRootMap;
            if (!map.ContainsKey(tid))
            {
                map[tid] = new GoalEntry(
                    tid,
                    fe.GoalBeforeHash,
                    vectors.TryGetValue(fe.GoalBeforeHash, out var v) ? v : []);
            }
        }

        var trainRoots = trainRootMap.Values.OrderBy(g => g.TheoremId, StringComparer.Ordinal).ToList();
        var evalRoots  = evalRootMap.Values.OrderBy(g => g.TheoremId, StringComparer.Ordinal).ToList();

        Console.WriteLine($"[RlbGraphLoader] root goals: {trainRoots.Count:N0} train, {evalRoots.Count:N0} eval");
        return (trainRoots, evalRoots);
    }

    private static float[] ParseVector(object? raw)
    {
        if (raw is null) return [];
        try
        {
            var list = ((IEnumerable<object>)raw).ToList();
            if (list.Count == 0) return [];
            var arr = new float[list.Count];
            for (int i = 0; i < list.Count; i++)
                arr[i] = Convert.ToSingle(list[i]);
            return arr;
        }
        catch
        {
            return [];
        }
    }

    public async ValueTask DisposeAsync() => await _driver.DisposeAsync();
}
