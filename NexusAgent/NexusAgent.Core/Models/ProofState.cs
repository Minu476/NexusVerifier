namespace NexusAgent.Core.Models;

/// <summary>
/// Snapshot of a Lean proof state at a specific point in an episode.
/// Encoded by ProofStateEncoder into a 64-dim vector for fossil matching.
/// </summary>
public sealed record ProofState
{
    public required string[] PendingGoals { get; init; }
    public required string[] Hypotheses { get; init; }
    public required string[] TacticHistory { get; init; }
    public required int SorryCount { get; init; }
    public required string[] ErrorMessages { get; init; }
    public required string DomainTag { get; init; }
    public required string SketchHash { get; init; }

    public static ProofState Empty(string domain) => new()
    {
        PendingGoals = [],
        Hypotheses = [],
        TacticHistory = [],
        SorryCount = 0,
        ErrorMessages = [],
        DomainTag = domain,
        SketchHash = string.Empty,
    };
}
