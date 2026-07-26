namespace NexusAgent.ToposExperiment.NarySearch;

/// <summary>
/// Outcome of one n-ary backward-chaining episode. Structurally identical to
/// <see cref="Search.ProofSearchResult"/> — the search contract (success/steps/terminal-node/
/// outcome) is independent of how the graph is stored. Separate type so the n-ary and projected
/// paths can't be accidentally mixed.
/// </summary>
public sealed record NaryProofSearchResult(
    bool IsSuccess,
    int Steps,
    string? TerminalNodeId,
    NaryProofSearchOutcome Outcome);

public enum NaryProofSearchOutcome { Success, DeadEnd, Timeout, Cycle }

public static class NaryProofSearch
{
    public static NaryProofSearchResult Success(int steps, string terminalNodeId) =>
        new(true, steps, terminalNodeId, NaryProofSearchOutcome.Success);

    public static NaryProofSearchResult Leaf(string terminalNodeId) =>
        new(true, 0, terminalNodeId, NaryProofSearchOutcome.Success);

    public static NaryProofSearchResult DeadEnd(string? lastNodeId) =>
        new(false, 0, lastNodeId, NaryProofSearchOutcome.DeadEnd);

    public static NaryProofSearchResult Timeout(string? lastNodeId) =>
        new(false, 0, lastNodeId, NaryProofSearchOutcome.Timeout);
}
