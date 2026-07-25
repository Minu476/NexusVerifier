namespace NexusAgent.ToposExperiment.Search;

/// <summary>Outcome of one backward-chaining episode. Verbatim from RlbExperiment.</summary>
public sealed record ProofSearchResult(
    bool IsSuccess,
    int Steps,
    string? TerminalNodeId,     // last trajectory node ID; caller uses for BackwardReinforce
    ProofSearchOutcome Outcome);

public enum ProofSearchOutcome { Success, DeadEnd, Timeout, Cycle }

public static class ProofSearch
{
    public static ProofSearchResult Success(int steps, string terminalNodeId) =>
        new(true, steps, terminalNodeId, ProofSearchOutcome.Success);

    public static ProofSearchResult Leaf(string terminalNodeId) =>
        new(true, 0, terminalNodeId, ProofSearchOutcome.Success);

    public static ProofSearchResult DeadEnd(string? lastNodeId) =>
        new(false, 0, lastNodeId, ProofSearchOutcome.DeadEnd);

    public static ProofSearchResult Timeout(string? lastNodeId) =>
        new(false, 0, lastNodeId, ProofSearchOutcome.Timeout);
}
