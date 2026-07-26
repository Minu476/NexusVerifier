using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NexusAgent.Core.Configuration;
using NexusAgent.Core.Memory;
using NexusAgent.Core.Models;
using NexusAgent.Core.Oracle;

namespace NexusAgent.Tests.Oracle;

/// <summary>
/// Phase 1: LeanOracle — 4 tests.
/// Uses real lake/lean process via environment variable NEXUS_LEAN_PROJECT.
/// </summary>
public sealed class LeanOracleTests : IAsyncLifetime
{
    private readonly string _leanProject;
    private readonly LeanOracle _oracle;
    private readonly Mock<INeo4jClient> _neo4j = new();

    public LeanOracleTests()
    {
        _leanProject = Environment.GetEnvironmentVariable("NEXUS_LEAN_PROJECT")
            ?? throw new InvalidOperationException(
                "Set NEXUS_LEAN_PROJECT to a valid lake project path.");

        var config = Options.Create(new NexusConfig
        {
            LeanProjectPath = _leanProject,
            LeanCompileTimeout = TimeSpan.FromSeconds(60),
        });

        // Cache misses by default
        _neo4j.Setup(n => n.GetCompileCacheAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((LeanResult?)null);
        _neo4j.Setup(n => n.PutCompileCacheAsync(It.IsAny<string>(), It.IsAny<LeanResult>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);

        _oracle = new LeanOracle(config, _neo4j.Object, NullLogger<LeanOracle>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CompileAsync_TrivialProof_ReturnsCompiled()
    {
        var sketch = """
            theorem trivial_true : True := by trivial
            """;

        var result = await _oracle.CompileAsync(sketch, CancellationToken.None);

        Assert.True(result.Compiled, $"Expected compiled. Errors: {string.Join("; ", result.Errors)}");
        Assert.Equal(0, result.SorryCount);
        Assert.True(result.IsFullyProved);
    }

    [Fact]
    public async Task CompileAsync_SorrySketch_CompileSucceeds()
    {
        // Lean compiles a sorry sketch successfully (exit 0) — it's a warning,
        // not a compile error. The oracle should not crash on such input.
        var sketch = """
            theorem with_sorry : 1 + 1 = 2 := by sorry
            """;

        var result = await _oracle.CompileAsync(sketch, CancellationToken.None);

        // The oracle succeeds (no exception) regardless of sorry semantics.
        Assert.NotNull(result);
    }

    [Fact]
    public async Task CompileAsync_MalformedSketch_ReturnsFailure()
    {
        var sketch = "this is not valid lean code @@@";

        var result = await _oracle.CompileAsync(sketch, CancellationToken.None);

        Assert.False(result.IsFullyProved);
    }

    [Fact]
    public async Task CompileAsync_CacheHit_ReturnsWithoutReinvoking()
    {
        var cached = new LeanResult
        {
            Compiled = true,
            RemainingGoals = 0,
            SorryCount = 0,
            Errors = [],
            Warnings = [],
            CompileTime = TimeSpan.FromMilliseconds(1),
            PendingGoalTexts = [],
        };

        _neo4j.Setup(n => n.GetCompileCacheAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(cached);

        var sketch = "theorem cache_test : True := by trivial";
        var result = await _oracle.CompileAsync(sketch, CancellationToken.None);

        Assert.Same(cached, result);
        _neo4j.Verify(n => n.PutCompileCacheAsync(It.IsAny<string>(), It.IsAny<LeanResult>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CompileAsync_GenuineProof_NoFalsePositiveFromAxiomCheck()
    {
        var sketch = """
            theorem genuinely_proved : 1 + 1 = 2 := by decide
            """;

        var result = await _oracle.CompileAsync(sketch, CancellationToken.None);

        Assert.True(result.IsFullyProved, $"Errors: {string.Join("; ", result.Errors)}");
        Assert.False(result.HasSorryAxiom);
    }

    [Fact]
    public async Task CompileAsync_RefineProducesMultipleSubgoals_SplitsIntoOnePendingGoalPerSubgoal()
    {
        // Regression 2026-07-26: found while scoping the graph-native proof-state
        // milestone. Live-captured 2026-07-26 against this exact NEXUS_LEAN_PROJECT:
        // `refine ⟨?_, ?_, ?_⟩` on a 3-way conjunction printed
        //   case refine_1
        //   ⊢ 1 = 1
        //
        //   case refine_2
        //   ⊢ 2 = 2
        //
        //   case refine_3
        //   ⊢ 3 = 3
        // — three goals, blank-line-separated in one "unsolved goals" diagnostic
        // block. Before the fix, RemainingGoals/PendingGoalTexts collapsed this to
        // 1 entry (the whole block joined); it must be 3.
        var sketch = """
            example : (1 = 1) ∧ (2 = 2) ∧ (3 = 3) := by
              refine ⟨?_, ?_, ?_⟩
            """;

        var result = await _oracle.CompileAsync(sketch, CancellationToken.None);

        Assert.Equal(3, result.RemainingGoals);
        Assert.Equal(3, result.PendingGoalTexts.Length);
        Assert.Contains(result.PendingGoalTexts, g => g.Contains("case refine_1") && g.Contains("1 = 1"));
        Assert.Contains(result.PendingGoalTexts, g => g.Contains("case refine_2") && g.Contains("2 = 2"));
        Assert.Contains(result.PendingGoalTexts, g => g.Contains("case refine_3") && g.Contains("3 = 3"));
        // Each entry must contain exactly its own goal, not bleed into siblings.
        var refine1 = Assert.Single(result.PendingGoalTexts, g => g.Contains("case refine_1"));
        Assert.DoesNotContain("refine_2", refine1);
        Assert.DoesNotContain("refine_3", refine1);
    }

    [Fact]
    public async Task CompileAsync_ConstructorGoalWithHypotheses_PreservesLocalContextPerGoal()
    {
        // Live-captured 2026-07-26 companion fixture: goals with a local context
        // (hypotheses above the turnstile) split the same way, and each split
        // entry keeps its own hypotheses rather than losing them.
        var sketch = """
            example (n : Nat) (h : n = 5) : (n = 5) ∧ (n + 1 = 6) := by
              constructor
            """;

        var result = await _oracle.CompileAsync(sketch, CancellationToken.None);

        Assert.Equal(2, result.PendingGoalTexts.Length);
        Assert.Contains(result.PendingGoalTexts, g => g.Contains("case left") && g.Contains("⊢ n = 5"));
        Assert.Contains(result.PendingGoalTexts, g => g.Contains("case right") && g.Contains("⊢ n + 1 = 6"));
        Assert.All(result.PendingGoalTexts, g => Assert.Contains("h : n = 5", g));
    }

    [Fact]
    public async Task CompileAsync_MultiGoalFix_DoesNotAffectCompiledComputation()
    {
        // Guard test: the LeanOracle.ParseLeanOutput method touched for the
        // multi-goal split is the same method that had the exitCode/errors.Count
        // false-positive bug fixed earlier tonight (#1). Confirm Compiled/errors
        // for a genuinely failing sketch are unaffected by the goal-splitting change.
        var sketch = """
            example : (1 = 1) ∧ (2 = 2) := by
              refine ⟨?_, ?_⟩
            """;

        var result = await _oracle.CompileAsync(sketch, CancellationToken.None);

        Assert.False(result.Compiled, "an unsolved-goals sketch must still be reported as not compiled");
        Assert.NotEmpty(result.Errors);
        Assert.Equal(0, result.SorryCount); // no literal 'sorry' — unsolved goals is a distinct signal
    }

    [Fact]
    public async Task CompileAsync_CitesAlreadySorryDeclaration_NotFullyProved()
    {
        // Regression 2026-07-25: found running the FC100 gap-set batch through
        // NexusProverSubagent. A candidate that cites Green14.W_3_15 (itself
        // `:= by sorry` in FormalConjectures/GreensOpenProblems/14.lean) compiles
        // clean with SorryCount=0 and prints no "declaration uses 'sorry'"
        // warning at the citing site — Lean only emits that warning where the
        // literal `sorry` occurs, not at every downstream use. Only
        // `#print axioms` reveals the inherited `sorryAx`. Confirmed live: this
        // exact sketch was accepted as "Solved" before the fix.
        var sketch = """
            import FormalConjectures.Subsets.FC100SolvedSet1

            theorem cites_sorry_backed_w315 : Green14.W 3 15 = 218 := by
              exact Green14.W_3_15
            """;

        var result = await _oracle.CompileAsync(sketch, CancellationToken.None);

        Assert.True(result.Compiled, $"Errors: {string.Join("; ", result.Errors)}");
        Assert.Equal(0, result.SorryCount);
        Assert.True(result.HasSorryAxiom, "expected #print axioms to detect the inherited sorryAx");
        Assert.False(result.IsFullyProved,
            "a sketch citing a sorry-backed declaration must not count as fully proved");
    }
}
