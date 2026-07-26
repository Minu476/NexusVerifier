namespace NexusAgent.ToposExperiment.Models;

/// <summary>One theorem root goal used as an episode start point. Verbatim from RlbExperiment.</summary>
public sealed record GoalEntry(
    string TheoremId,
    string GoalHash,        // 64-char hex, no prefix (OF-1)
    float[] StateVector);   // 64-dim float; empty when not available
