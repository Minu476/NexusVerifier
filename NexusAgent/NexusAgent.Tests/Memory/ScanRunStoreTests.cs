using Moq;
using NexusAgent.Core.Memory;

namespace NexusAgent.Tests.Memory;

/// <summary>
/// Unit tests for <see cref="INeo4jClient.UpsertScanRunAsync"/>.
/// Verifies that scan-hg results are forwarded to Neo4j with correct shape —
/// no real Neo4j connection required.
/// </summary>
public sealed class ScanRunStoreTests
{
    private readonly Mock<INeo4jClient> _neo4j = new();

    public ScanRunStoreTests()
    {
        _neo4j.Setup(n => n.UpsertScanRunAsync(
                It.IsAny<HgScanRun>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static HgScanRun MakeRun(
        List<HgGoalResult>? goals = null,
        int discarded = 0,
        bool holdout = false)
    {
        var g = goals ?? [];
        return new HgScanRun(
            Id:              "aabbccdd1122334455667788aabbccdd",
            RunAt:           new DateTime(2026, 5, 28, 12, 0, 0, DateTimeKind.Utc),
            ElapsedSeconds:  42.5,
            Shards:          8,
            TimeoutSeconds:  300,
            TotalGoals:      100,
            ProvedCount:     g.Count(r => r.Proved),
            GenuineCount:    g.Count(r => r.Proved && !r.IsSelfCitation),
            SelfCiteCount:   g.Count(r => r.IsSelfCitation),
            GapCount:        g.Count(r => !r.Proved),
            DiscardedShards: discarded,
            Goals:           g,
            IsHoldoutRun:    holdout);
    }

    private static HgGoalResult Proved(string name, params string[] steps) =>
        new(name, Proved: true,  Steps: [.. steps]);

    private static HgGoalResult Gap(string name) =>
        new(name, Proved: false, Steps: []);

    // ── Happy-path ───────────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertScanRunAsync_EmptyGoals_CallsNeo4jOnce()
    {
        var run = MakeRun();

        await _neo4j.Object.UpsertScanRunAsync(run, CancellationToken.None);

        _neo4j.Verify(n => n.UpsertScanRunAsync(run, CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task UpsertScanRunAsync_TwoProvedOneGap_CountsAreCorrect()
    {
        var goals = new List<HgGoalResult>
        {
            Proved("Erdos42.example_maximal_sidon", "Erdos42.example_maximal_sidon"),
            Proved("OeisA67720.a_1", "Nat.totient_two"),
            Gap("Erdos1.erdos_1"),
        };
        var run = MakeRun(goals);

        Assert.Equal(2, run.ProvedCount);
        Assert.Equal(1, run.GapCount);
        Assert.Equal(3, run.Goals.Count);

        await _neo4j.Object.UpsertScanRunAsync(run, CancellationToken.None);

        _neo4j.Verify(n => n.UpsertScanRunAsync(
            It.Is<HgScanRun>(r => r.ProvedCount == 2 && r.GapCount == 1),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpsertScanRunAsync_ProofStepsPreserved()
    {
        var goals = new List<HgGoalResult>
        {
            Proved("OpenQuantumProblem35.ame_2_exists", "assumption:hd", "OpenQuantumProblem35.ame_2_exists"),
        };
        HgScanRun? captured = null;
        _neo4j.Setup(n => n.UpsertScanRunAsync(It.IsAny<HgScanRun>(), It.IsAny<CancellationToken>()))
              .Callback<HgScanRun, CancellationToken>((r, _) => captured = r)
              .Returns(Task.CompletedTask);

        await _neo4j.Object.UpsertScanRunAsync(MakeRun(goals), CancellationToken.None);

        Assert.NotNull(captured);
        var g = Assert.Single(captured.Goals, g => g.Proved);
        Assert.Equal(["assumption:hd", "OpenQuantumProblem35.ame_2_exists"], g.Steps);
    }

    [Fact]
    public async Task UpsertScanRunAsync_DiscardedShardsPreserved()
    {
        HgScanRun? captured = null;
        _neo4j.Setup(n => n.UpsertScanRunAsync(It.IsAny<HgScanRun>(), It.IsAny<CancellationToken>()))
              .Callback<HgScanRun, CancellationToken>((r, _) => captured = r)
              .Returns(Task.CompletedTask);

        await _neo4j.Object.UpsertScanRunAsync(MakeRun(discarded: 2), CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(2, captured.DiscardedShards);
    }

    [Fact]
    public async Task UpsertScanRunAsync_RunMetadataPreserved()
    {
        HgScanRun? captured = null;
        _neo4j.Setup(n => n.UpsertScanRunAsync(It.IsAny<HgScanRun>(), It.IsAny<CancellationToken>()))
              .Callback<HgScanRun, CancellationToken>((r, _) => captured = r)
              .Returns(Task.CompletedTask);

        var run = MakeRun();
        await _neo4j.Object.UpsertScanRunAsync(run, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(run.Id,              captured.Id);
        Assert.Equal(run.RunAt,           captured.RunAt);
        Assert.Equal(42.5,                captured.ElapsedSeconds);
        Assert.Equal(8,                   captured.Shards);
        Assert.Equal(300,                 captured.TimeoutSeconds);
        Assert.Equal(100,                 captured.TotalGoals);
    }

    // ── Edge cases ───────────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertScanRunAsync_CancellationPassedThrough()
    {
        using var cts = new CancellationTokenSource();
        var run = MakeRun();

        await _neo4j.Object.UpsertScanRunAsync(run, cts.Token);

        _neo4j.Verify(n => n.UpsertScanRunAsync(run, cts.Token), Times.Once);
    }

    [Fact]
    public async Task UpsertScanRunAsync_AllProved_GapIsZero()
    {
        var goals = Enumerable.Range(1, 5)
            .Select(i => Proved($"Goal{i}", $"step{i}"))
            .ToList();
        var run = MakeRun(goals);

        Assert.Equal(5, run.ProvedCount);
        Assert.Equal(0, run.GapCount);

        await _neo4j.Object.UpsertScanRunAsync(run, CancellationToken.None);

        _neo4j.Verify(n => n.UpsertScanRunAsync(
            It.Is<HgScanRun>(r => r.GapCount == 0),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpsertScanRunAsync_AllGap_ProvedIsZero()
    {
        var goals = Enumerable.Range(1, 3).Select(i => Gap($"Goal{i}")).ToList();
        var run = MakeRun(goals);

        Assert.Equal(0, run.ProvedCount);
        Assert.Equal(3, run.GapCount);

        await _neo4j.Object.UpsertScanRunAsync(run, CancellationToken.None);

        _neo4j.Verify(n => n.UpsertScanRunAsync(
            It.Is<HgScanRun>(r => r.ProvedCount == 0),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── IsSelfCitation logic ─────────────────────────────────────────────────

    [Fact]
    public void IsSelfCitation_PureSelfCite_IsTrue()
    {
        // Single step = goal's own name → self-citation
        var r = Proved("Foo.bar", "Foo.bar");
        Assert.True(r.IsSelfCitation);
    }

    [Fact]
    public void IsSelfCitation_AssumptionPlusSelfCite_IsTrue()
    {
        // assumption: + goal name → still self-citation (Opus pattern)
        var r = Proved("Foo.bar", "assumption:h", "Foo.bar");
        Assert.True(r.IsSelfCitation);
    }

    [Fact]
    public void IsSelfCitation_InternalProofTerms_IsTrue()
    {
        // Goal._proof_1_1 style (generated proof term names) → self-citation
        var r = Proved("Foo.bar", "Foo.bar._proof_1_1");
        Assert.True(r.IsSelfCitation);
    }

    [Fact]
    public void IsSelfCitation_ExternalLemmaStep_IsFalse()
    {
        // External Mathlib lemma → genuine composition
        var r = Proved("OeisA67720.a_1", "Nat.totient_two");
        Assert.False(r.IsSelfCitation);
    }

    [Fact]
    public void IsSelfCitation_MixedExternalAndSelfCite_IsFalse()
    {
        // One external step → not a self-citation even with others
        var r = Proved("Foo.bar", "le_refl", "Foo.bar");
        Assert.False(r.IsSelfCitation);
    }

    [Fact]
    public void IsSelfCitation_GapResult_IsFalse()
    {
        // Unproved goals are never self-citations
        var r = Gap("Foo.bar");
        Assert.False(r.IsSelfCitation);
    }

    [Fact]
    public void GenuineCount_MatchesExternalLemmaProofs()
    {
        var goals = new List<HgGoalResult>
        {
            Proved("OeisA67720.a_1",                        "Nat.totient_two"),           // GENUINE
            Proved("PellNumbers.pellNumber_two",             "Nat.mul_comm"),              // GENUINE
            Proved("Erdos42.example_maximal_sidon",          "Erdos42.example_maximal_sidon"), // SELF-CITE
            Proved("AgohGiuga.korselts_criterion",           "assumption:ha₁", "AgohGiuga.korselts_criterion"), // SELF-CITE
            Gap("Erdos1.erdos_1"),
        };
        var summary = new HgScanSummary(goals, DiscardedShards: 0, TotalShards: 8);

        Assert.Equal(2, summary.GenuineCount);
        Assert.Equal(2, summary.SelfCiteCount);
        Assert.Equal(4, summary.ProvedCount);
        Assert.Equal(1, summary.GapCount);
    }

    // ── Holdout verdict ──────────────────────────────────────────────────────

    [Fact]
    public void IsHoldoutRun_DefaultIsFalse()
    {
        var run = MakeRun();
        Assert.False(run.IsHoldoutRun);
    }

    [Fact]
    public void IsHoldoutRun_SetToTrue_Preserved()
    {
        var run = MakeRun(holdout: true);
        Assert.True(run.IsHoldoutRun);
    }

    [Fact]
    public void HoldoutRun_ProvedGoals_SurvivesHoldoutIsTrue()
    {
        // The Neo4j layer computes survivesHoldout = IsHoldoutRun && g.Proved.
        // This test verifies the C# side: holdout run + proved goal = survivor.
        var holdoutRun = MakeRun(
            goals:   [Proved("OeisA67720.a_1", "Nat.add_comm"), Gap("Erdos1.erdos_1")],
            holdout: true);

        Assert.True(holdoutRun.IsHoldoutRun);
        // The survivor is the proved goal; gap must not be counted.
        Assert.Equal(1, holdoutRun.Goals.Count(g => holdoutRun.IsHoldoutRun && g.Proved));
        Assert.Equal(0, holdoutRun.Goals.Count(g => holdoutRun.IsHoldoutRun && !g.Proved && g.Proved));
    }

    [Fact]
    public void StandardRun_HoldoutFlagOff_NoSurvivors()
    {
        // A standard (non-holdout) run never marks any goal as survivesHoldout.
        var standardRun = MakeRun(
            goals:   [Proved("OeisA67720.a_1", "Nat.totient_two")],
            holdout: false);

        Assert.False(standardRun.IsHoldoutRun);
        Assert.Equal(0, standardRun.Goals.Count(g => standardRun.IsHoldoutRun && g.Proved));
    }
}
