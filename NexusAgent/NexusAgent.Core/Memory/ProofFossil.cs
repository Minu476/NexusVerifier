namespace NexusAgent.Core.Memory;

public sealed record ProofFossil
{
    public required string Id { get; init; }
    public required string SubgoalText { get; init; }
    public required string TacticBlock { get; init; }
    public required float[] StateVector { get; init; }
    public required string DomainTag { get; init; }
    public required int SorryCountBefore { get; init; }
    public required int SorryCountAfter { get; init; }
    public required DateTime ProvedAt { get; init; }
    public required string[] SourceProblems { get; init; }
    public required int UseCount { get; init; }
    public bool CompilationVerified { get; init; } = true;
    /// <summary>Run identifier of the process that created this fossil.</summary>
    public string? RunId { get; init; }
    /// <summary>True if this fossil was reused by a later run (cross-run compounding).</summary>
    public bool CrossRun { get; init; }
}

public sealed record FossilMatch(
    ProofFossil Fossil,
    float Similarity);
