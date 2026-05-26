using NexusAgent.Core.Models;

namespace NexusAgent.Core.Planning;

public sealed record ProofLandmark
{
    public required string Id { get; init; }
    public required string ProblemId { get; init; }
    public required float[] StateVector { get; init; }
    public required int SorryCount { get; init; }
    public required int VisitCount { get; init; }
    public required int DeadEndCount { get; init; }
    public required TransitionOutcome BestOutcome { get; init; }

    /// <summary>
    /// Potential = (1 - dead_end_fraction) * (1 / sqrt(visit_count)).
    /// Higher = more promising landmark to resume search from.
    /// </summary>
    public float Potential
    {
        get
        {
            if (VisitCount == 0) return 1f;
            var deadEndFrac = (float)DeadEndCount / VisitCount;
            return (1f - deadEndFrac) / MathF.Sqrt(VisitCount);
        }
    }
}
