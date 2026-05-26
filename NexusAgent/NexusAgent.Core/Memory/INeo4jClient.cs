using NexusAgent.Core.Models;
using NexusAgent.Core.Planning;

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
    Task IncrementFossilUseCountAsync(string fossilId, CancellationToken ct);
    Task<int> CountFossilsAsync(CancellationToken ct);

    // ---- Landmark graph ----
    Task<ProofLandmark> UpsertLandmarkAsync(ProofLandmark landmark, CancellationToken ct);
    Task RecordTransitionAsync(
        string fromLandmarkId, string toLandmarkId,
        string tacticSequence, TransitionOutcome outcome,
        string episodeId, CancellationToken ct);
    Task<IReadOnlyList<ProofLandmark>> NearbyLandmarksAsync(
        float[] queryVector, int topK, CancellationToken ct);

    // ---- Compile cache ----
    Task<LeanResult?> GetCompileCacheAsync(string sketchHash, CancellationToken ct);
    Task PutCompileCacheAsync(string sketchHash, LeanResult result, CancellationToken ct);

    // ---- Problem registry ----
    Task UpsertProblemAsync(string id, string source, string leanFilePath, CancellationToken ct);
    Task MarkProblemSolvedAsync(string id, int episodesUsed, CancellationToken ct);

    Task EnsureSchemaAsync(CancellationToken ct);
}
