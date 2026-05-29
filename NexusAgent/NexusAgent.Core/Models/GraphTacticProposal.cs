namespace NexusAgent.Core.Models;

/// <summary>
/// Deterministic tactic candidate proposed from the offline GoalShape -> APPLIES graph.
/// </summary>
public sealed record GraphTacticProposal
{
    public required string TacticId { get; init; }
    public required string TacticText { get; init; }
    public required float NearestGoalSimilarity { get; init; }
    public required float HistoricalSuccessRate { get; init; }
    public required int SupportCount { get; init; }
    public required float RankScore { get; init; }
}
