namespace NexusAgent.MathlibIngestor.Models;

public sealed record IngestRecord(
    string? ModuleSource,
    string TheoremName,
    string? TheoremNamespace,
    string? TheoremStatement,
    string GoalBefore,
    IReadOnlyList<string> GoalsAfter,
    string TacticRaw,
    IReadOnlyList<string> Premises,
    bool Success);
