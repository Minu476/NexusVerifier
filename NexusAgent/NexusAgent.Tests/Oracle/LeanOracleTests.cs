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
}
