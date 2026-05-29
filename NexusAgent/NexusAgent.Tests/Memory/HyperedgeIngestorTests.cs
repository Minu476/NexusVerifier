using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NexusAgent.Core.Memory;

namespace NexusAgent.Tests.Memory;

/// <summary>
/// Unit tests for <see cref="HyperedgeIngestor.IngestAsync"/>.
/// Exercises JSONL parsing (including edge-cases) using a mock
/// <see cref="INeo4jClient"/> — no real Neo4j connection required.
/// </summary>
public sealed class HyperedgeIngestorTests : IAsyncLifetime
{
    private readonly Mock<INeo4jClient> _neo4j = new();
    private readonly HyperedgeIngestor _ingestor;
    private readonly string _tempDir;

    public HyperedgeIngestorTests()
    {
        _neo4j.Setup(n => n.UpsertHyperedgesAsync(
                It.IsAny<IEnumerable<HyperedgeRecord>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _ingestor = new HyperedgeIngestor(_neo4j.Object, NullLogger<HyperedgeIngestor>.Instance);
        _tempDir = Path.Combine(Path.GetTempPath(), $"nexus-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        Directory.Delete(_tempDir, recursive: true);
        return Task.CompletedTask;
    }

    // ── Happy-path ───────────────────────────────────────────────────────────

    [Fact]
    public async Task IngestAsync_TwoValidEdges_UpsertsTwo()
    {
        var path = WriteLines(
            """{"fn":"Nat.add_comm","inputs":[],"output":"n + m = m + n"}""",
            """{"fn":"Nat.mul_comm","inputs":[],"output":"n * m = m * n"}""");

        var count = await _ingestor.IngestAsync(path, CancellationToken.None);

        Assert.Equal(2, count);
        _neo4j.Verify(n => n.UpsertHyperedgesAsync(
            It.Is<IEnumerable<HyperedgeRecord>>(edges => edges.Count() == 2),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IngestAsync_EdgeWithInputs_ParsedCorrectly()
    {
        var path = WriteLines(
            """{"fn":"dvd_trans","inputs":["a ∣ b","b ∣ c"],"output":"a ∣ c"}""");

        IReadOnlyList<HyperedgeRecord>? captured = null;
        _neo4j.Setup(n => n.UpsertHyperedgesAsync(
                It.IsAny<IEnumerable<HyperedgeRecord>>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<HyperedgeRecord>, CancellationToken>(
                (edges, _) => captured = edges.ToList())
            .Returns(Task.CompletedTask);

        await _ingestor.IngestAsync(path, CancellationToken.None);

        var edge = Assert.Single(captured!);
        Assert.Equal("dvd_trans", edge.LemmaName);
        Assert.Equal("a ∣ c", edge.Output);
        Assert.Equal(2, edge.Inputs.Length);
        Assert.Equal("a ∣ b", edge.Inputs[0]);
    }

    [Fact]
    public async Task IngestAsync_StableId_SameInputSameId()
    {
        var line = """{"fn":"Nat.add_comm","inputs":[],"output":"n + m = m + n"}""";
        var path1 = WriteLines(line);
        var path2 = WriteLines(line);

        var capturedEdges = new List<HyperedgeRecord>();
        _neo4j.Setup(n => n.UpsertHyperedgesAsync(
                It.IsAny<IEnumerable<HyperedgeRecord>>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<HyperedgeRecord>, CancellationToken>(
                (edges, _) => capturedEdges.AddRange(edges))
            .Returns(Task.CompletedTask);

        await _ingestor.IngestAsync(path1, CancellationToken.None);
        await _ingestor.IngestAsync(path2, CancellationToken.None);

        // ID is deterministic across runs — Neo4j MERGE will be idempotent.
        Assert.Equal(2, capturedEdges.Count);
        Assert.Equal(capturedEdges[0].Id, capturedEdges[1].Id);
    }

    // ── Resilience ───────────────────────────────────────────────────────────

    [Fact]
    public async Task IngestAsync_BlankLinesAndInvalidLines_SkipsGracefully()
    {
        var path = WriteLines(
            "",
            "   ",
            "not json at all",
            """{"fn":"Nat.add_comm","inputs":[],"output":"n + m = m + n"}""",
            "{bad");

        var count = await _ingestor.IngestAsync(path, CancellationToken.None);

        Assert.Equal(1, count);
        _neo4j.Verify(n => n.UpsertHyperedgesAsync(
            It.Is<IEnumerable<HyperedgeRecord>>(edges => edges.Count() == 1),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IngestAsync_MissingFnField_LineSkipped()
    {
        var path = WriteLines(
            """{"inputs":[],"output":"something"}""",
            """{"fn":"Nat.add_comm","inputs":[],"output":"n + m = m + n"}""");

        var count = await _ingestor.IngestAsync(path, CancellationToken.None);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task IngestAsync_MissingOutputField_LineSkipped()
    {
        var path = WriteLines(
            """{"fn":"Nat.add_comm","inputs":[]}""",
            """{"fn":"Nat.mul_comm","inputs":[],"output":"n * m = m * n"}""");

        var count = await _ingestor.IngestAsync(path, CancellationToken.None);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task IngestAsync_EmptyFile_UpsertsZero()
    {
        var path = WriteLines();

        var count = await _ingestor.IngestAsync(path, CancellationToken.None);

        Assert.Equal(0, count);
        _neo4j.Verify(n => n.UpsertHyperedgesAsync(
            It.Is<IEnumerable<HyperedgeRecord>>(edges => !edges.Any()),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IngestAsync_FileNotFound_Throws()
    {
        var missing = Path.Combine(_tempDir, "does_not_exist.jsonl");

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _ingestor.IngestAsync(missing, CancellationToken.None));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string WriteLines(params string[] lines)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }
}
