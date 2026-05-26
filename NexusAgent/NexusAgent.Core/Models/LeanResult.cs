namespace NexusAgent.Core.Models;

/// <summary>
/// Binary verdict from LeanOracle on a proof sketch.
/// Compiled=true with SorryCount=0 means the theorem is fully proved.
/// </summary>
public sealed record LeanResult
{
    public required bool Compiled { get; init; }
    public required int RemainingGoals { get; init; }
    public required int SorryCount { get; init; }
    public required string[] Errors { get; init; }
    public required string[] Warnings { get; init; }
    public required TimeSpan CompileTime { get; init; }
    public required string[] PendingGoalTexts { get; init; }

    public bool IsFullyProved => Compiled && SorryCount == 0 && RemainingGoals == 0;

    public static LeanResult Failure(string error, TimeSpan elapsed) => new()
    {
        Compiled = false,
        RemainingGoals = -1,
        SorryCount = -1,
        Errors = [error],
        Warnings = [],
        CompileTime = elapsed,
        PendingGoalTexts = [],
    };
}
