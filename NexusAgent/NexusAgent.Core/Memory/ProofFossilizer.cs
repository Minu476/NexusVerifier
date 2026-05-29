using Microsoft.Extensions.Logging;
using NexusAgent.Core.Encoding;
using NexusAgent.Core.Models;

namespace NexusAgent.Core.Memory;

/// <summary>
/// Persists newly-proven sub-goals to the Neo4j fossil vault and retrieves
/// matching fossils for new proof states. Equivalent role to FsdeFossilizer
/// in the FSDE project but specialized to Lean proof sub-goals.
/// </summary>
public sealed class ProofFossilizer
{
    private readonly INeo4jClient _neo4j;
    private readonly ProofStateEncoder _encoder;
    private readonly ILogger<ProofFossilizer> _log;

    /// <summary>
    /// Per-process run identifier. Each dotnet invocation gets a unique ID so
    /// fossils created in this run can be distinguished from those reused from prior runs.
    /// </summary>
    public static readonly string RunId = Guid.NewGuid().ToString("N")[..8];

    public ProofFossilizer(
        INeo4jClient neo4j,
        ProofStateEncoder encoder,
        ILogger<ProofFossilizer> log)
    {
        _neo4j = neo4j;
        _encoder = encoder;
        _log = log;
    }

    public async Task<string> FossilizeAsync(
        ProofState stateBefore,
        string subgoalText,
        string tacticBlock,
        int sorryReduction,
        string sourceProblem,
        CancellationToken ct)
    {
        var fossil = new ProofFossil
        {
            Id = Guid.NewGuid().ToString("N"),
            SubgoalText = subgoalText,
            TacticBlock = tacticBlock,
            StateVector = _encoder.Encode(stateBefore),
            DomainTag = stateBefore.DomainTag,
            SorryCountBefore = stateBefore.SorryCount,
            SorryCountAfter = Math.Max(0, stateBefore.SorryCount - sorryReduction),
            ProvedAt = DateTime.UtcNow,
            SourceProblems = [sourceProblem],
            UseCount = 0,
            CompilationVerified = true,
            RunId = RunId,
        };

        await _neo4j.UpsertFossilAsync(fossil, ct);
        _log.LogInformation("Fossilized subgoal {Id} (Δsorry={Delta}) for problem {Problem}",
            fossil.Id[..8], sorryReduction, sourceProblem);
        return fossil.Id;
    }

    /// <summary>
    /// Look for fossils that could fill in current sorry positions.
    /// Returns matches above the configured threshold ordered by similarity.
    /// </summary>
    public async Task<IReadOnlyList<FossilMatch>> FindCandidatesAsync(
        ProofState currentState,
        int topK,
        float minSimilarity,
        CancellationToken ct)
    {
        var vec = _encoder.Encode(currentState);
        var matches = await _neo4j.NearestFossilsAsync(vec, topK, minSimilarity, ct);

        var filtered = matches
            .Where(match => match.Similarity >= minSimilarity)
            .Select(match =>
            {
                // Keep unverified fossils in the retrieval horizon, but rank them lower.
                // Applying a hard post-weight threshold can starve the fallback path.
                var verificationWeight = match.Fossil.CompilationVerified ? 1f : 0.75f;
                var adjustedSimilarity = match.Similarity * verificationWeight;
                return match with { Similarity = adjustedSimilarity };
            })
            .OrderByDescending(match => match.Similarity)
            .Take(topK)
            .ToArray();

        if (filtered.Length > 0)
        {
            _log.LogDebug("Fossil retrieval: {Count} matches, top={Top:F3}",
                filtered.Length, filtered[0].Similarity);
        }
        return filtered;
    }

    public async Task RecordUseAsync(string fossilId, CancellationToken ct)
        => await _neo4j.IncrementFossilUseCountAsync(fossilId, RunId, ct);
}
