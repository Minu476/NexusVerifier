namespace NexusAgent.ToposExperiment.Models;

/// <summary>
/// One APPLIES edge row from Neo4j. Verbatim copy of
/// <c>NexusAgent.RlbExperiment.Models.AppliesEdge</c> — same schema, same OF-1 verbatim-hash rule,
/// so <see cref="Ingest.RlbGraphLoader"/> consumes it identically. Namespace only is changed.
/// </summary>
public sealed record AppliesEdge(
    string GoalBeforeHash,
    string GoalAfterHash,
    string TacticId,
    string TheoremId,
    string Split,           // "train" | "eval"
    int Count,
    int SuccessSum,
    float[] GoalBeforeVector,   // stateVector from GoalShape node; empty if absent
    float[] GoalAfterVector);   // stateVector from GoalShape node; empty if absent
