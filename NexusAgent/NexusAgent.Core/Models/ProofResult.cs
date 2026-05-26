namespace NexusAgent.Core.Models;

/// <summary>
/// Final result of an Orchestrator run on a single problem.
/// </summary>
public sealed record ProofResult
{
    public required string ProblemId { get; init; }
    public required ProofOutcome Outcome { get; init; }
    public required string? FinalSketch { get; init; }
    public required int EpisodesUsed { get; init; }
    public required int TurnsUsed { get; init; }
    public required int FossilHits { get; init; }
    public required int LlmCallsTier1 { get; init; }
    public required int LlmCallsTier2 { get; init; }
    public required int LlmCallsTier3 { get; init; }
    public required decimal EstimatedCostUsd { get; init; }
    public required TimeSpan TotalDuration { get; init; }
    public required string[] FossilsCreated { get; init; }
}

public enum ProofOutcome
{
    Solved,
    EpisodeBudgetExhausted,
    TimedOut,
    Aborted,
    LeanEnvironmentError,
}

/// <summary>
/// Outcome label written to a landmark transition. Cartographer uses these
/// to detect dead-end regions of the proof search space.
/// </summary>
public enum TransitionOutcome
{
    Progressed,   // sorry count decreased or new hypothesis discharged
    Stalled,      // compiled but no progress
    DeadEnd,      // compile failure that resembles prior failures
    Solved,       // theorem completed
}

/// <summary>
/// Which LLM tier handled a turn. Used for cost tracking and analysis.
/// </summary>
public enum LlmTier
{
    Tier0_FossilHit = 0,
    Tier1_QwenLocal = 1,
    Tier2_DeepSeekFlash = 2,
    Tier3_PremiumCloud = 3,
}
