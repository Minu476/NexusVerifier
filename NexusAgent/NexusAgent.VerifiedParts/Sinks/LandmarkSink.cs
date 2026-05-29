using Microsoft.Extensions.Logging;
using NexusAgent.Core.Models;
using NexusAgent.Core.Planning;
using NexusAgent.VerifiedParts.Models;

namespace NexusAgent.VerifiedParts.Sinks;

/// <summary>
/// Sink that injects a verified part into the landmark/replay layer.
///
/// This is "Idea 2" — landmarks are retrieved by <c>NearbySolvedLandmarksAsync</c>
/// and replayed by the planner's Tier 0.5 episode loop via <c>ShortestSuccessfulPathAsync</c>.
///
/// The sink records two landmarks and a transition:
///   open  (SorryCount=1, BestOutcome=Progressed) — the unsolved goal state
///   solved (SorryCount=0, BestOutcome=Solved)    — after applying the proof block
///   transition open→solved labelled with the proof block as the tactic sequence
///
/// Cross-problem replay limitation (known): <c>ComputeLandmarkId</c> is problem-scoped,
/// so <c>ShortestSuccessfulPathAsync</c> won't return paths across different ProblemIds.
/// However, <c>NearbySolvedLandmarksAsync</c> uses vector similarity and IS cross-problem,
/// so the solved landmark IS visible to similar goals from any problem.
/// </summary>
public sealed class LandmarkSink : IPartSink
{
    public string Name => "landmark";

    private readonly ProofCartographer    _cartographer;
    private readonly ILogger<LandmarkSink> _log;

    public LandmarkSink(ProofCartographer cartographer, ILogger<LandmarkSink> log)
    {
        _cartographer = cartographer;
        _log          = log;
    }

    public async Task<string> WriteAsync(
        VerifiedPart part, string[] axioms, ProofState openGoal, CancellationToken ct)
    {
        // ── 1. Register the "before" state ───────────────────────────────────
        // One sorry open, goal is the part's statement. Outcome=Progressed because
        // this is a state from which we know we can make progress (we have the proof).
        await _cartographer.ObserveAsync(openGoal, part.ProblemId, TransitionOutcome.Progressed, ct);

        // ── 2. Register the "solved" state ───────────────────────────────────
        var solvedGoal = openGoal with
        {
            PendingGoals = [],
            SorryCount   = 0,
        };
        var solvedLandmark = await _cartographer.ObserveAsync(
            solvedGoal, part.ProblemId, TransitionOutcome.Solved, ct);

        // ── 3. Record the transition open→solved ──────────────────────────────
        await _cartographer.RecordTransitionAsync(
            fromState:       openGoal,
            toState:         solvedGoal,
            problemId:       part.ProblemId,
            tacticSequence:  part.ProofBlock,
            outcome:         TransitionOutcome.Solved,
            episodeId:       VerifiedPartIngestor.IngestRunId,
            ct:              ct);

        _log.LogDebug("LandmarkSink: {LmId} ← {Part}", solvedLandmark.Id[..8], part.PartName);
        return solvedLandmark.Id;
    }
}
