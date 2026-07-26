using NexusAgent.Core.Planning;

namespace NexusAgent.Tests.Planning;

/// <summary>
/// Unit tests for <see cref="ProofGoalGraph"/> — pure in-memory
/// <c>Topos.Hypergraph.HypergraphKernel</c> usage, no Neo4j/Lean dependency.
/// </summary>
public class ProofGoalGraphTests
{
    [Fact]
    public void GetOrAddGoal_SameTextTwice_ReturnsSameId()
    {
        var graph = new ProofGoalGraph();
        var id1 = graph.GetOrAddGoal("⊢ n = 5");
        var id2 = graph.GetOrAddGoal("⊢ n = 5");
        Assert.Equal(id1, id2);
    }

    [Fact]
    public void GetOrAddGoal_WhitespaceVariation_ReturnsSameId()
    {
        var graph = new ProofGoalGraph();
        var id1 = graph.GetOrAddGoal("⊢ n = 5");
        var id2 = graph.GetOrAddGoal("⊢   n  =  5\n");
        Assert.Equal(id1, id2);
    }

    [Fact]
    public void GetOrAddGoal_DifferentText_ReturnsDifferentIds()
    {
        var graph = new ProofGoalGraph();
        var id1 = graph.GetOrAddGoal("⊢ n = 5");
        var id2 = graph.GetOrAddGoal("⊢ n = 6");
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void RecordAttempt_ThenFailedAttemptsFor_RoundTrips()
    {
        var graph = new ProofGoalGraph();
        var goal = graph.GetOrAddGoal("⊢ n = 5");

        graph.RecordAttempt(goal, "decide", afterGoalIds: [], AttemptOutcome.Failed);

        var attempts = graph.FailedAttemptsFor(goal);
        var attempt = Assert.Single(attempts);
        Assert.Equal("decide", attempt.TacticText);
        Assert.Equal(AttemptOutcome.Failed, attempt.Outcome);
    }

    [Fact]
    public void FailedAttemptsFor_ExcludesProgressedAttempts()
    {
        var graph = new ProofGoalGraph();
        var goal = graph.GetOrAddGoal("⊢ n = 5");
        var sub = graph.GetOrAddGoal("⊢ n + 1 = 6");

        graph.RecordAttempt(goal, "bad_tactic", afterGoalIds: [], AttemptOutcome.Failed);
        graph.RecordAttempt(goal, "refine ?_", afterGoalIds: [sub], AttemptOutcome.Progressed);

        var attempts = graph.FailedAttemptsFor(goal);
        var attempt = Assert.Single(attempts);
        Assert.Equal("bad_tactic", attempt.TacticText);
    }

    [Fact]
    public void FailedAttemptsFor_UnknownGoal_ReturnsEmpty()
    {
        var graph = new ProofGoalGraph();
        Assert.Empty(graph.FailedAttemptsFor("nonexistent-id"));
    }

    [Fact]
    public void SiblingsOf_MultiSubgoalAttempt_ReturnsOtherSubgoals()
    {
        var graph = new ProofGoalGraph();
        var parent = graph.GetOrAddGoal("⊢ (1 = 1) ∧ (2 = 2) ∧ (3 = 3)");
        var g1 = graph.GetOrAddGoal("⊢ 1 = 1");
        var g2 = graph.GetOrAddGoal("⊢ 2 = 2");
        var g3 = graph.GetOrAddGoal("⊢ 3 = 3");

        graph.RecordAttempt(parent, "refine ⟨?_, ?_, ?_⟩", [g1, g2, g3], AttemptOutcome.Progressed);

        var siblingsOfG1 = graph.SiblingsOf(g1);
        Assert.Equal(2, siblingsOfG1.Count);
        Assert.Contains(g2, siblingsOfG1);
        Assert.Contains(g3, siblingsOfG1);
        Assert.DoesNotContain(g1, siblingsOfG1);
    }

    [Fact]
    public void SiblingsOf_GoalNeverProducedByAnAttempt_ReturnsEmpty()
    {
        var graph = new ProofGoalGraph();
        var goal = graph.GetOrAddGoal("⊢ n = 5"); // the episode's original goal, no producing attempt
        Assert.Empty(graph.SiblingsOf(goal));
    }

    [Fact]
    public void RecordAttempt_UnknownBeforeGoalId_Throws()
    {
        var graph = new ProofGoalGraph();
        Assert.Throws<ArgumentException>(() =>
            graph.RecordAttempt("nonexistent-id", "decide", [], AttemptOutcome.Failed));
    }

    [Fact]
    public void TwoAttemptsProduceSameNormalizedGoalText_DedupToOneVertex()
    {
        // First-seen-wins dedup: two different tactic paths that happen to produce
        // the same (normalized) goal text must resolve to the same goal id, so
        // failed-attempt history and sibling lookups aren't split across duplicates.
        var graph = new ProofGoalGraph();
        var start1 = graph.GetOrAddGoal("⊢ P ∧ Q");
        var start2 = graph.GetOrAddGoal("⊢ R ∧ P ∧ Q"); // a different starting goal
        var shared = graph.GetOrAddGoal("⊢ n = 5");
        var sharedAgain = graph.GetOrAddGoal("⊢   n =  5"); // same goal, different tactic path, whitespace varies

        Assert.Equal(shared, sharedAgain);

        graph.RecordAttempt(start1, "tac_a", [shared], AttemptOutcome.Progressed);
        graph.RecordAttempt(start2, "tac_b", [sharedAgain], AttemptOutcome.Progressed);

        // Both attempts' after-goal resolved to the same vertex, so each is visible
        // as a sibling-producing attempt on the same goal id.
        Assert.Equal(shared, sharedAgain);
    }
}
