using System.Security.Cryptography;
using NexusAgent.Core.Agent;
using Topos.Hypergraph;

namespace NexusAgent.Core.Planning;

/// <summary>
/// Per-<c>SolveAsync</c>-call memory of every open goal seen this run and every
/// tactic attempt fired on it, backed by a real <see cref="HypergraphKernel"/> —
/// not a shape-mirroring stand-in. Same edge-vertex + incidence pattern already
/// established by <c>NexusAgent.ToposExperiment/Ingest/ToposAppliesAdapter.cs</c>
/// for tactic applications: one <see cref="VertexRoles.Edge"/> vertex per attempt,
/// one incidence to the goal it fired on (<see cref="BeforeRole"/>), one incidence
/// per subgoal it produced (<see cref="AfterRole"/>).
///
/// This is additive telemetry/memory for enriching the LLM prompt — it must never
/// influence <c>Compiled</c>/<c>SorryCount</c>/<c>IsFullyProved</c>/the structural
/// gate. Callers record attempts only after those have already run; this class
/// never re-derives trust, only remembers what already happened.
///
/// Lifecycle: <see cref="NexusOrchestrator"/>/<see cref="NexusProverSubagent"/> are
/// DI singletons that <c>nexus bench</c> calls concurrently across different
/// problems (see <c>SemaphoreSlim</c>-bounded parallelism in <c>Program.cs</c>).
/// Neither class holds mutable instance state, and this class must not either —
/// one instance is created as a local variable per <c>NexusOrchestrator.SolveAsync</c>
/// call and threaded through <see cref="Agent.NexusProverSubagent"/>'s
/// <c>EpisodeContext</c>, never stored as a field on either singleton.
/// </summary>
public sealed class ProofGoalGraph
{
    /// <summary>The goal-state before a tactic fires — V2/ToposAppliesAdapter's "Anchor"
    /// equivalent. Same role-byte convention as <c>ToposAppliesAdapter.BeforeRole</c>,
    /// reused deliberately, not reinvented — a different consumer, same meaning.</summary>
    public const byte BeforeRole = 0;

    /// <summary>Each subgoal a tactic produces. Same convention as
    /// <c>ToposAppliesAdapter.AfterRole</c>.</summary>
    public const byte AfterRole = 1;

    private readonly HypergraphKernel _kernel = new();
    private readonly Dictionary<string, Handle> _goalHandlesByHash = new(StringComparer.Ordinal);

    private readonly PropertyKey<string> _goalTextProp;
    private readonly PropertyKey<string> _tacticTextProp;
    private readonly PropertyKey<AttemptOutcome> _outcomeProp;

    public ProofGoalGraph()
    {
        _goalTextProp = _kernel.ResolveProperty<string>("goalText");
        _tacticTextProp = _kernel.ResolveProperty<string>("tacticText");
        _outcomeProp = _kernel.ResolveProperty<AttemptOutcome>("outcome");
    }

    /// <summary>
    /// Returns the stable goal ID for <paramref name="goalText"/> — first-seen-wins:
    /// the same normalized text always maps to the same ID and the same underlying
    /// vertex, whichever tactic path produced it first. ID is a SHA-256 hex hash of
    /// the whitespace-normalized text (same normalization <see cref="SketchValidator"/>
    /// uses for signature comparison, and the same hex-hash-key convention
    /// <c>ProofCartographer.ComputeLandmarkId</c> already uses elsewhere in this
    /// codebase — deliberately consistent, not a new convention).
    /// </summary>
    public string GetOrAddGoal(string goalText)
    {
        var id = HashGoalText(goalText);
        if (!_goalHandlesByHash.TryGetValue(id, out var handle))
        {
            handle = _kernel.CreateVertex();
            _goalHandlesByHash[id] = handle;
            _kernel.SetProperty(_goalTextProp, handle, goalText);
        }
        else
        {
            DedupHits++;
        }
        return id;
    }

    /// <summary>How many times <see cref="GetOrAddGoal"/> resolved to an already-seen
    /// goal rather than creating a new vertex — telemetry only, surfaced via
    /// <c>EpisodeResult</c>/<c>ProofResult</c> so benchmark runs can measure whether
    /// goal-history memory is actually reducing repeated dead-end tactics before a
    /// heavier Milestone B is justified.</summary>
    public int DedupHits { get; private set; }

    /// <summary>
    /// Records one tactic attempt as an edge-vertex: an incidence to
    /// <paramref name="beforeGoalId"/> (<see cref="BeforeRole"/>) and one incidence
    /// per entry in <paramref name="afterGoalIds"/> (<see cref="AfterRole"/>, in
    /// order). Both IDs must already exist via <see cref="GetOrAddGoal"/> — this
    /// method doesn't create goal vertices, only attempt edges between them.
    ///
    /// No hypothesize-then-promote lifecycle (Topos's <c>AssertionMode</c> is
    /// deliberately not used here — see the class doc on
    /// <see cref="AttemptOutcome"/> for why): by the time a caller has an
    /// <paramref name="outcome"/> to pass, the compile result and the structural
    /// gate have already run synchronously, so the outcome is already known.
    /// Record it directly.
    /// </summary>
    public void RecordAttempt(
        string beforeGoalId,
        string tacticText,
        IReadOnlyList<string> afterGoalIds,
        AttemptOutcome outcome)
    {
        if (!_goalHandlesByHash.TryGetValue(beforeGoalId, out var beforeHandle))
            throw new ArgumentException(
                $"Unknown goal id '{beforeGoalId}' — call {nameof(GetOrAddGoal)} first.",
                nameof(beforeGoalId));

        var edge = _kernel.CreateVertex(VertexRoles.Edge);
        _kernel.AddIncidence(edge, beforeHandle, BeforeRole, ordinal: 0);
        for (int i = 0; i < afterGoalIds.Count; i++)
        {
            if (!_goalHandlesByHash.TryGetValue(afterGoalIds[i], out var afterHandle))
                throw new ArgumentException(
                    $"Unknown goal id '{afterGoalIds[i]}' — call {nameof(GetOrAddGoal)} first.",
                    nameof(afterGoalIds));
            _kernel.AddIncidence(edge, afterHandle, AfterRole, ordinal: i + 1);
        }

        _kernel.SetProperty(_tacticTextProp, edge, tacticText);
        _kernel.SetProperty(_outcomeProp, edge, outcome);
    }

    /// <summary>
    /// Every previously-attempted, non-<see cref="AttemptOutcome.Progressed"/> tactic
    /// recorded against <paramref name="goalId"/> — what the LLM should be told not to
    /// repeat. Hand-rolled role-filtered walk over <c>IncidencesOf</c>/property lookup,
    /// same idiom <c>NaryBackwardChainer.CandidateEdgesFor</c> already uses elsewhere in
    /// this codebase; <see cref="IHypergraphQuery"/> deliberately has no built-in for
    /// this ("the kernel does not judge" — role-aware traversal is a layer-1 concern).
    /// </summary>
    public IReadOnlyList<FailedAttempt> FailedAttemptsFor(string goalId)
    {
        if (!_goalHandlesByHash.TryGetValue(goalId, out var goalHandle))
            return [];

        var results = new List<FailedAttempt>();
        foreach (var inc in _kernel.IncidencesOf(goalHandle))
        {
            if (inc.Role != BeforeRole) continue; // this goal was the target of an attempt, not its source
            if (!_kernel.TryGetProperty(_outcomeProp, inc.Source, out var outcome)) continue;
            if (outcome == AttemptOutcome.Progressed) continue;
            _kernel.TryGetProperty(_tacticTextProp, inc.Source, out var tacticText);
            results.Add(new FailedAttempt(tacticText ?? "(unknown tactic)", outcome));
        }
        return results;
    }

    /// <summary>
    /// Every other goal produced by the same tactic attempt(s) that produced
    /// <paramref name="goalId"/> — the sibling-subgoal set a `refine ⟨?_, ?_⟩`-style
    /// tactic creates. Empty if <paramref name="goalId"/> was never itself the
    /// after-side of a recorded attempt (e.g. it's the episode's original goal).
    /// </summary>
    public IReadOnlyList<string> SiblingsOf(string goalId)
    {
        if (!_goalHandlesByHash.TryGetValue(goalId, out var goalHandle))
            return [];

        var handleToId = _goalHandlesByHash
            .GroupBy(kv => kv.Value)
            .ToDictionary(g => g.Key, g => g.First().Key);

        var siblings = new List<string>();
        foreach (var inc in _kernel.IncidencesOf(goalHandle))
        {
            if (inc.Role != AfterRole) continue; // this goal was produced by an attempt, find its siblings
            foreach (var sibling in _kernel.IncidencesFrom(inc.Source))
            {
                if (sibling.Role != AfterRole) continue;
                if (sibling.Member == goalHandle) continue;
                if (handleToId.TryGetValue(sibling.Member, out var siblingId))
                    siblings.Add(siblingId);
            }
        }
        return siblings.Distinct(StringComparer.Ordinal).ToList();
    }

    private static string HashGoalText(string goalText)
    {
        var normalized = SketchValidator.NormalizeWhitespace(goalText);
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

/// <summary>
/// Outcome of one tactic attempt against one goal. Deliberately a distinct type
/// from Topos's <c>AssertionMode</c> (Asserted/Quoted/Hypothesized — epistemic
/// status of an asserted fact, not pass/fail of a search attempt, and it has no
/// "Failed" state) and from <c>NexusAgent.Core.Models.TransitionOutcome</c>
/// (Solved/DeadEnd/Stalled/Progressed — whole-episode/whole-sketch semantics: a
/// single goal closes, it doesn't get "fully proved" the way a whole sketch does).
/// Conflating whole-episode "looks solved" with per-goal "this attempt closed" is
/// exactly the kind of scope-blurring that produced the sorry-citation-laundering
/// bug found 2026-07-25 — keep these distinct even though they overlap conceptually.
/// </summary>
public enum AttemptOutcome
{
    Failed,
    CompiledNoProgress,
    Progressed,
}

/// <summary>One previously-attempted tactic and its outcome, for prompt display.</summary>
public sealed record FailedAttempt(string TacticText, AttemptOutcome Outcome);
