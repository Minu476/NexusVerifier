using NexusAgent.Core.Models;

namespace NexusAgent.Core.Planning;

/// <summary>
/// Cross-run, Topos-backed tactic retrieval. Returns candidate tactics for a given
/// proof-state vector (the same role the Neo4j goal-shape graph plays for Tier 0.75),
/// and records outcomes so future problems benefit from this one's experience.
///
/// Backed by a process-lifetime <c>Topos.Hypergraph.HypergraphKernel</c> with a native
/// <c>VectorIndex</c> for goal-similarity search and <c>EdgeStatistics</c> for per-tactic
/// success tracking. This is the "full advantage of Topos" path: one Edge-vertex per
/// (goal, tactic) group, n-ary incidences, immutable-value + kernel-as-truth mutation —
/// the pattern blessed by <c>external/Topos/docs/NEXUS_VERIFIER_INTEGRATION_FINDINGS.md</c>
/// ("What worked" #3) and proven by <c>NexusAgent.ToposExperiment/Ingest/ToposAppliesAdapter</c>.
///
/// Distinct from <see cref="NexusAgent.Core.Planning.ProofGoalGraph"/>: that is per-episode
/// telemetry (one kernel per <c>SolveAsync</c>, thrown away after); this is cross-problem
/// memory (one kernel per process, shared across all problems in a <c>nexus bench</c> run).
/// </summary>
public interface IToposTacticStore
{
    /// <summary>
    /// Propose candidate tactics for the given query vector, ranked by goal-similarity ×
    /// historical success. Same return contract as
    /// <c>Neo4jClient.ProposeTacticsFromGoalVectorAsync</c> — drop-in replacement for the
    /// Tier 0.75 call site.
    /// </summary>
    Task<IReadOnlyList<GraphTacticProposal>> ProposeAsync(
        float[] queryVector, int neighborK, int topK, CancellationToken ct);

    /// <summary>
    /// Record that a tactic fired against a goal and either reduced sorry (success) or
    /// didn't (failure). Reinforces the edge's <c>EdgeStatistics</c> via the read-modify-write
    /// pattern (read current → <c>Observe</c> → <c>SetProperty</c> back), so the same
    /// tactic ranks higher/lower next time. Thread-safe (single-writer lock; reads are
    /// already safe under the kernel's SWMR contract). The goal is hashed internally with the
    /// same SHA-256 + NormalizeWhitespace convention the store seeds with, so the caller only
    /// needs to pass the raw goal text.
    /// </summary>
    Task RecordOutcomeAsync(string tacticId, string goalText, bool succeeded, CancellationToken ct);

    /// <summary>
    /// Upsert a tactic→goal edge into the store (creating it if absent) and record a success
    /// outcome. Called from the fossilization path so EVERY successful sorry-reduction during a
    /// run enriches the store for later problems — not just ones the store itself proposed.
    /// If the edge already exists (e.g. it was proposed and won), this just reinforces it.
    /// </summary>
    Task RecordSuccessAsync(string goalText, float[] goalVector, string tacticText, CancellationToken ct);

    /// <summary>
    /// Seed the store from existing <see cref="NexusAgent.Core.Memory.ProofFossil"/> records
    /// (the prior run's proven tactic applications). Each fossil becomes one goal vertex (with
    /// its 64-dim <c>StateVector</c>) and one tactic edge (with <c>EdgeStatistics</c> seeded to
    /// success=1,count=1, since the fossil only exists because it once reduced sorry).
    /// </summary>
    Task SeedFromFossilsAsync(System.Collections.Generic.IEnumerable<Memory.ProofFossil> fossils, CancellationToken ct);

    /// <summary>Number of goal vertices currently in the store (telemetry).</summary>
    int GoalCount { get; }

    /// <summary>Number of tactic edge-vertices currently in the store (telemetry).</summary>
    int TacticEdgeCount { get; }
}
