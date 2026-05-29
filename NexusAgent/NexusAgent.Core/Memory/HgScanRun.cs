namespace NexusAgent.Core.Memory;

/// <summary>
/// One complete <c>scan-hg</c> execution, persisted to Neo4j as
/// <c>(:HgScanRun)-[:HAS_RESULT]-&gt;(:HgGoalResult)</c> nodes.
///
/// <para>Useful queries:</para>
/// <code>
/// // Progress over time
/// MATCH (r:HgScanRun) RETURN r.runAt, r.provedCount ORDER BY r.runAt
///
/// // All proved goals in the latest run
/// MATCH (r:HgScanRun) WITH r ORDER BY r.runAt DESC LIMIT 1
/// MATCH (r)-[:HAS_RESULT]->(g:HgGoalResult {proved: true})
/// RETURN g.goalName, g.steps
///
/// // Goals newly proved vs. previous run
/// MATCH (r1:HgScanRun), (r2:HgScanRun)
/// WHERE r1.runAt &lt; r2.runAt
/// WITH r1, r2 ORDER BY r2.runAt DESC LIMIT 1
/// MATCH (r2)-[:HAS_RESULT]->(g2 {proved: true})
/// WHERE NOT EXISTS { MATCH (r1)-[:HAS_RESULT]->(g1 {goalName: g2.goalName, proved: true}) }
/// RETURN g2.goalName
/// </code>
/// </summary>
public sealed record HgScanRun(
    string                      Id,
    DateTime                    RunAt,
    double                      ElapsedSeconds,
    int                         Shards,
    int                         TimeoutSeconds,
    int                         TotalGoals,
    int                         ProvedCount,
    int                         GenuineCount,
    int                         SelfCiteCount,
    int                         GapCount,
    int                         DiscardedShards,
    IReadOnlyList<HgGoalResult> Goals,
    bool                        IsHoldoutRun = false);
