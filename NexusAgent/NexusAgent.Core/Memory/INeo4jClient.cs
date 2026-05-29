using NexusAgent.Core.Models;
using NexusAgent.Core.Planning;
using FossilAnalysis = NexusAgent.Core.Models.FossilAnalysis;

namespace NexusAgent.Core.Memory;

/// <summary>
/// Single interface for all Neo4j access. Production replacement: swap with
/// RichLearning.V2.Memory.Neo4jGraphMemory once the project reference is wired
/// in NexusAgent.Core.csproj.
/// </summary>
public interface INeo4jClient
{
    // ---- Fossil vault ----
    Task UpsertFossilAsync(ProofFossil fossil, CancellationToken ct);
    Task<IReadOnlyList<FossilMatch>> NearestFossilsAsync(
        float[] queryVector, int topK, float minSimilarity, CancellationToken ct);
    /// <summary>
    /// Returns tactic candidates from the pre-ingested GoalShape/APPLIES graph by
    /// nearest-neighbor lookup over GoalShape vectors, then aggregating outgoing tactics.
    /// </summary>
    Task<IReadOnlyList<GraphTacticProposal>> ProposeTacticsFromGoalVectorAsync(
        float[] queryVector, int neighborK, int topK, CancellationToken ct);
    Task IncrementFossilUseCountAsync(string fossilId, string currentRunId, CancellationToken ct);
    Task<int> CountFossilsAsync(CancellationToken ct);
    /// <summary>Phase 8: full fossil vault analysis for reporting.</summary>
    Task<FossilAnalysis> FossilAnalysisAsync(CancellationToken ct);

    // ---- Landmark graph ----
    Task<ProofLandmark> UpsertLandmarkAsync(ProofLandmark landmark, CancellationToken ct);
    Task RecordTransitionAsync(
        string fromLandmarkId, string toLandmarkId,
        string tacticSequence, TransitionOutcome outcome,
        string episodeId, CancellationToken ct);
    Task<IReadOnlyList<ProofLandmark>> NearbyLandmarksAsync(
        float[] queryVector, int topK, CancellationToken ct);
    /// <summary>
    /// Like <see cref="NearbyLandmarksAsync"/> but restricted to landmarks whose
    /// <c>bestOutcome</c> is <c>'Solved'</c>. Used by Tier 0.5 to find replay targets.
    /// </summary>
    Task<IReadOnlyList<ProofLandmark>> NearbySolvedLandmarksAsync(
        float[] queryVector, int topK, CancellationToken ct);
    /// <summary>
    /// Returns the ordered list of <c>tacticSequence</c> strings from each edge on the
    /// shortest path (up to 10 hops, only <c>Progressed</c>/<c>Solved</c> edges) from
    /// <paramref name="fromLandmarkId"/> to <paramref name="toLandmarkId"/>, or
    /// <c>null</c> if no such path exists.
    /// </summary>
    Task<IReadOnlyList<string>?> ShortestSuccessfulPathAsync(
        string fromLandmarkId, string toLandmarkId, CancellationToken ct);

    // ---- Compile cache ----
    Task<LeanResult?> GetCompileCacheAsync(string sketchHash, CancellationToken ct);
    Task PutCompileCacheAsync(string sketchHash, LeanResult result, CancellationToken ct);

    // ---- Problem registry ----
    Task UpsertProblemAsync(string id, string source, string leanFilePath, CancellationToken ct);
    Task MarkProblemSolvedAsync(string id, int episodesUsed, CancellationToken ct);
    Task<bool> IsProblemSolvedAsync(string id, CancellationToken ct);

    Task EnsureSchemaAsync(CancellationToken ct);

    // ---- ErdosHypergraph edge store ----

    /// <summary>
    /// Bulk-upsert a batch of hyperedges produced by <c>buildHypergraph</c>.
    /// Uses MERGE on <c>id</c> so repeated calls are idempotent.
    /// </summary>
    Task UpsertHyperedgesAsync(IEnumerable<HyperedgeRecord> edges, CancellationToken ct);

    /// <summary>
    /// Return all stored hyperedges. Used by the Lean warm-start writer
    /// to regenerate the JSONL cache without re-running <c>buildHypergraph</c>.
    /// </summary>
    Task<IReadOnlyList<HyperedgeRecord>> GetAllHyperedgesAsync(CancellationToken ct);

    // ---- Scan run log ----

    /// <summary>
    /// Persist one complete <c>scan-hg</c> execution to Neo4j.
    /// Creates <c>:HgScanRun</c> and <c>:HgGoalResult</c> nodes
    /// linked by <c>(:HgScanRun)-[:HAS_RESULT]-&gt;(:HgGoalResult)</c>.
    /// Idempotent on <see cref="HgScanRun.Id"/>.
    /// </summary>
    Task UpsertScanRunAsync(HgScanRun run, CancellationToken ct);
}
