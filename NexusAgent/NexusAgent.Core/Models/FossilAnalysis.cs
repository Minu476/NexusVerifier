namespace NexusAgent.Core.Models;

/// <summary>
/// Phase 8 fossil vault analysis — identifies universal lemmas, domain
/// distribution, and landmark topology accumulated across Phase 7 runs.
/// </summary>
public sealed record FossilAnalysis
{
    public required int TotalFossils { get; init; }
    public required int TotalLandmarks { get; init; }
    public required int SolvedProblems { get; init; }

    /// <summary>Top fossils ordered by reuse count (the "universal lemmas").</summary>
    public required IReadOnlyList<FossilSummary> TopFossils { get; init; }

    /// <summary>How many fossils fall in each domain tag.</summary>
    public required IReadOnlyDictionary<string, int> DomainDistribution { get; init; }

    /// <summary>
    /// Fossils that form the deepest PRECEDES chains — the backbone of reusable
    /// tactic sequences. Key = root fossil id, Value = chain length.
    /// </summary>
    public required IReadOnlyDictionary<string, int> DeepestPrecedesChains { get; init; }
    /// <summary>Number of fossils tagged as reused from a prior run (cross-run compounding).</summary>
    public required int CrossRunHits { get; init; }
}

public sealed record FossilSummary(
    string Id,
    string DomainTag,
    int UseCount,
    int SorryReduction,
    string SubgoalSnippet,
    string TacticSnippet,
    string[] SourceProblems);
