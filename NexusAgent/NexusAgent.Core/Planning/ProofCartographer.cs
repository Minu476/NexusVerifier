using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using NexusAgent.Core.Encoding;
using NexusAgent.Core.Memory;
using NexusAgent.Core.Models;

namespace NexusAgent.Core.Planning;

/// <summary>
/// Topological navigator for the proof search space. Replaces AlphaProof
/// Nexus's Elo-rated evolutionary sampling with deterministic graph-guided
/// selection — no rating agents needed.
///
/// Key responsibilities:
///   - Record every visited proof state as a landmark.
///   - Track transitions (tactic sequence → next state) with outcome labels.
///   - Detect structurally-exhausted regions (same state visited ≥ N times,
///     all dead-end outcomes) and produce a prompt hint to avoid them.
///   - Select the highest-potential resume point for a new episode.
/// </summary>
public sealed class ProofCartographer
{
    private readonly INeo4jClient _neo4j;
    private readonly ProofStateEncoder _encoder;
    private readonly ILogger<ProofCartographer> _log;
    private readonly CartographerConfig _cfg;

    public ProofCartographer(
        INeo4jClient neo4j,
        ProofStateEncoder encoder,
        ILogger<ProofCartographer> log,
        CartographerConfig? config = null)
    {
        _neo4j = neo4j;
        _encoder = encoder;
        _log = log;
        _cfg = config ?? new CartographerConfig();
    }

    public async Task<ProofLandmark> ObserveAsync(
        ProofState state,
        string problemId,
        TransitionOutcome outcome,
        CancellationToken ct)
    {
        var vec = _encoder.Encode(state);
        var id = ComputeLandmarkId(state, problemId);

        var landmark = new ProofLandmark
        {
            Id = id,
            ProblemId = problemId,
            StateVector = vec,
            SorryCount = state.SorryCount,
            VisitCount = 1,    // server-side ON MATCH increments
            DeadEndCount = 0,
            BestOutcome = outcome,
        };
        return await _neo4j.UpsertLandmarkAsync(landmark, ct);
    }

    public async Task RecordTransitionAsync(
        ProofState fromState,
        ProofState toState,
        string problemId,
        string tacticSequence,
        TransitionOutcome outcome,
        string episodeId,
        CancellationToken ct)
    {
        var fromId = ComputeLandmarkId(fromState, problemId);
        var toId = ComputeLandmarkId(toState, problemId);
        await _neo4j.RecordTransitionAsync(fromId, toId, tacticSequence, outcome, episodeId, ct);
    }

    /// <summary>
    /// Returns a human-readable hint for the LLM prompt, naming approaches that
    /// have repeatedly led to dead ends from a similar state.
    /// </summary>
    public async Task<string?> GetDeadEndHintAsync(ProofState state, CancellationToken ct)
    {
        var vec = _encoder.Encode(state);
        var nearby = await _neo4j.NearbyLandmarksAsync(vec, _cfg.NearbyLandmarkK, ct);

        var exhausted = nearby
            .Where(l =>
                ProofStateEncoder.CosineSimilarity(l.StateVector, vec) >= _cfg.NearMatchThreshold
                && l.VisitCount >= _cfg.MinVisitsForDeadEnd
                && (float)l.DeadEndCount / l.VisitCount >= _cfg.DeadEndFractionThreshold)
            .ToList();

        if (exhausted.Count == 0) return null;

        var msg =
            $"Cartographer hint: {exhausted.Count} similar proof state(s) " +
            $"have been visited {exhausted.Sum(e => e.VisitCount)} time(s) with " +
            $"{exhausted.Sum(e => e.DeadEndCount)} dead-end outcomes. Try a different " +
            $"decomposition strategy or attack the goal from a different angle.";

        _log.LogDebug("Dead-end hint generated for problem {Problem}",
            state.SketchHash.Length >= 8 ? state.SketchHash[..8] : state.SketchHash);
        return msg;
    }

    private static string ComputeLandmarkId(ProofState state, string problemId)
    {
        // Landmark identity: problem + sketch-hash + sorry count. Same proof
        // state in same problem maps to the same landmark.
        var key = $"{problemId}|{state.SketchHash}|{state.SorryCount}";
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }
}

public sealed record CartographerConfig
{
    public int NearbyLandmarkK { get; init; } = 8;
    public float NearMatchThreshold { get; init; } = 0.92f;
    public int MinVisitsForDeadEnd { get; init; } = 3;
    public float DeadEndFractionThreshold { get; init; } = 0.8f;
}
