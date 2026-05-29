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
    public required int FossilRetrievalSamples { get; init; }
    public required int LlmCallsTier1 { get; init; }
    public required int LlmCallsTier2 { get; init; }
    public required int LlmCallsTier3 { get; init; }
    public required decimal EstimatedCostUsd { get; init; }
    public required TimeSpan TotalDuration { get; init; }
    public required string[] FossilsCreated { get; init; }
    /// <summary>Average cosine similarity of fossil retrievals across all turns (0 if no fossils retrieved).</summary>
    public required float AvgFossilSimilarity { get; init; }
    /// <summary>
    /// Highest fossil similarity retrieved but NOT used for direct substitution (below threshold).
    /// On failed problems, values ≥ 0.85 indicate the encoder had a near-miss — useful for tuning.
    /// </summary>
    public required float BestMissedFossilSim { get; init; }
    /// <summary>Number of turns where a graph path replay succeeded without calling the LLM.</summary>
    public required int GraphReplayHits { get; init; }
    /// <summary>Episodes that ended early due to repeated structural gate violations.</summary>
    public required int StructuralGateRejections { get; init; }
    /// <summary>True when graph-first planner was used for this problem.</summary>
    public bool GraphPlannerUsed { get; init; }
    /// <summary>Number of planner frontier expansions attempted.</summary>
    public int GraphPlannerExpansions { get; init; }
    /// <summary>Number of planner transitions that passed Lean compile + structural checks.</summary>
    public int GraphPlannerAcceptedTransitions { get; init; }
    /// <summary>True when legacy LLM subagent was invoked after planner search.</summary>
    public bool LegacyLlmFallbackUsed { get; init; }
    public GraphProposalTelemetry Tier075Telemetry { get; init; } = new();
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
    Tier1_Cheap = 1,
    Tier2_DeepSeekFlash = 2,
    Tier3_PremiumCloud = 3,
    /// <summary>Graph path replay — no LLM call; tracked separately from llmCallsByTier[].</summary>
    Tier0_5_GraphReplay = 4,
    /// <summary>
    /// Dedicated hallucination-gate juror (e.g. Gemini 2.5 Flash).
    /// Not part of the main proof-generation ladder; used only by HallucinationGate
    /// for majority-vote lemma classification alongside Tier1_Cheap.
    /// </summary>
    Tier0_GateJuror = 5,
}
