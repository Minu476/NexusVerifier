using NexusAgent.Core.Models;

namespace NexusAgent.Core.Oracle;

/// <summary>
/// Binary ground-truth judge for proof sketches. Wraps the local Lean 4
/// compiler. The output of CompileAsync is the only authoritative signal in
/// the system — fossil matches, LLM outputs, and Cartographer hints are all
/// hypotheses until Lean accepts the proof.
/// </summary>
public interface ILeanOracle
{
    /// <summary>
    /// Compile a complete Lean sketch and return goal state.
    /// </summary>
    Task<LeanResult> CompileAsync(string leanSketch, CancellationToken ct);

    /// <summary>
    /// Check a single subgoal in isolation (used when fossil retrieval wants
    /// to verify whether a candidate tactic block actually closes a goal).
    /// </summary>
    Task<LeanResult> CheckSubgoalAsync(
        string goalStatement,
        string proofTactics,
        IEnumerable<string> imports,
        CancellationToken ct);
}
