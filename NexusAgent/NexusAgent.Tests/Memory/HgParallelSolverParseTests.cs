using Microsoft.Extensions.Logging.Abstractions;
using NexusAgent.Core.Memory;

namespace NexusAgent.Tests.Memory;

/// <summary>
/// Unit tests for <see cref="HgParallelSolver.ParseShardOutput"/>.
/// These tests exercise the sentinel / control-gate / result-parsing logic
/// without spawning any real Lean processes.
/// </summary>
public sealed class HgParallelSolverParseTests
{
    // A solver instance whose constructor is cheap — no real Lean work happens here.
    private readonly HgParallelSolver _solver = new(
        leanProjectPath: "/dev/null",
        engineFile: "/dev/null/ErdosHypergraph.lean",
        NullLogger<HgParallelSolver>.Instance);

    // ── Happy-path ───────────────────────────────────────────────────────────

    [Fact]
    public void Parse_ControlOk_SingleProved_ReturnsProvedResult()
    {
        var stdout = """
            ##NEXUS## {"type":"control","result":"ok"}
            ##NEXUS## {"type":"result","name":"Foo.bar","result":"PROVED","steps":[{"fn":"Nat.add_comm","goal":"n + m = m + n"}]}
            """;

        var result = _solver.ParseShardOutput(0, stdout, "");

        Assert.True(result.ControlPassed);
        Assert.False(result.TimedOut);
        Assert.Single(result.GoalResults);
        Assert.Equal("Foo.bar", result.GoalResults[0].Name);
        Assert.True(result.GoalResults[0].Proved);
        Assert.Equal("Nat.add_comm", result.GoalResults[0].Steps[0]);
    }

    [Fact]
    public void Parse_ControlOk_GapGoal_ReturnsGapResult()
    {
        var stdout = """
            ##NEXUS## {"type":"control","result":"ok"}
            ##NEXUS## {"type":"result","name":"Hard.conjecture","result":"GAP"}
            some stray Lean elaboration message
            """;

        var result = _solver.ParseShardOutput(0, stdout, "");

        Assert.True(result.ControlPassed);
        Assert.Single(result.GoalResults);
        Assert.Equal("Hard.conjecture", result.GoalResults[0].Name);
        Assert.False(result.GoalResults[0].Proved);
        Assert.Empty(result.GoalResults[0].Steps);
    }

    [Fact]
    public void Parse_ControlOk_MultipleGoals_AggregatesAll()
    {
        var stdout = """
            ##NEXUS## {"type":"control","result":"ok"}
            ##NEXUS## {"type":"result","name":"A","result":"PROVED","steps":[]}
            ##NEXUS## {"type":"result","name":"B","result":"GAP"}
            ##NEXUS## {"type":"result","name":"C","result":"PROVED","steps":[{"fn":"Nat.mul_comm","goal":"a * b = b * a"}]}
            """;

        var result = _solver.ParseShardOutput(1, stdout, "");

        Assert.True(result.ControlPassed);
        Assert.Equal(3, result.GoalResults.Count);
        Assert.Equal(2, result.GoalResults.Count(r => r.Proved));
        Assert.Equal(1, result.GoalResults.Count(r => !r.Proved));
    }

    // ── Soundness-gate failures ──────────────────────────────────────────────

    [Fact]
    public void Parse_ControlFail_DiscardsAllResults()
    {
        var stdout = """
            ##NEXUS## {"type":"control","result":"FAIL"}
            ##NEXUS## {"type":"result","name":"Foo.bar","result":"PROVED","steps":[]}
            """;

        var result = _solver.ParseShardOutput(0, stdout, "");

        Assert.False(result.ControlPassed);
        Assert.Empty(result.GoalResults);
    }

    [Fact]
    public void Parse_NoSentinelLines_Discards()
    {
        var stdout = "some lean output without the sentinel\nmore lines\n";

        var result = _solver.ParseShardOutput(2, stdout, "");

        Assert.False(result.ControlPassed);
        Assert.Empty(result.GoalResults);
    }

    [Fact]
    public void Parse_MalformedFirstLine_Discards()
    {
        var stdout = """
            ##NEXUS## not-valid-json{{{
            ##NEXUS## {"type":"result","name":"Foo","result":"PROVED","steps":[]}
            """;

        var result = _solver.ParseShardOutput(0, stdout, "");

        Assert.False(result.ControlPassed);
        Assert.Empty(result.GoalResults);
    }

    [Fact]
    public void Parse_ControlOk_MalformedResultLine_SkipsLine()
    {
        var stdout = """
            ##NEXUS## {"type":"control","result":"ok"}
            ##NEXUS## BAD_JSON
            ##NEXUS## {"type":"result","name":"Good","result":"PROVED","steps":[]}
            """;

        var result = _solver.ParseShardOutput(0, stdout, "");

        Assert.True(result.ControlPassed);
        // Bad line is skipped; good line parsed
        Assert.Single(result.GoalResults);
        Assert.Equal("Good", result.GoalResults[0].Name);
    }

    [Fact]
    public void Parse_StrayLinesOutsideSentinel_AreIgnored()
    {
        var stdout = """
            lake: warning: buildHypergraph starting
            ##NEXUS## {"type":"control","result":"ok"}
            [shard-ctrl] GAP (correct): 1 = 2
            ##NEXUS## {"type":"result","name":"Target","result":"GAP"}
            done
            """;

        var result = _solver.ParseShardOutput(0, stdout, "");

        Assert.True(result.ControlPassed);
        Assert.Single(result.GoalResults);
        Assert.False(result.GoalResults[0].Proved);
    }
}
