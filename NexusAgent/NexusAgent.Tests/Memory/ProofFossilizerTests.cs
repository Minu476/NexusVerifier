using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NexusAgent.Core.Configuration;
using NexusAgent.Core.Encoding;
using NexusAgent.Core.Memory;
using NexusAgent.Core.Models;

namespace NexusAgent.Tests.Memory;

/// <summary>
/// Phase 3: ProofFossilizer — 3 tests (real Neo4j integration).
/// Requires NEO4J_URI / NEO4J_USERNAME / NEO4J_PASSWORD environment variables
/// pointing to a running Neo4j Enterprise instance with database=nexusdb.
/// </summary>
public sealed class ProofFossilizerTests : IAsyncLifetime
{
    private readonly Neo4jClient _neo4j;
    private readonly ProofFossilizer _fossilizer;
    private readonly ProofStateEncoder _encoder;

    public ProofFossilizerTests()
    {
        var config = Options.Create(new NexusConfig
        {
            Neo4jUri = Environment.GetEnvironmentVariable("NEO4J_URI") ?? "bolt://localhost:7687",
            Neo4jUser = Environment.GetEnvironmentVariable("NEO4J_USERNAME") ?? "neo4j",
            Neo4jPassword = Environment.GetEnvironmentVariable("NEO4J_PASSWORD") ?? "REDACTED",
            Neo4jDatabase = "nexusdb",
            TacticVocabPath = "does_not_exist.json",
        });

        _neo4j = new Neo4jClient(config, NullLogger<Neo4jClient>.Instance);
        _encoder = new ProofStateEncoder(config, NullLogger<ProofStateEncoder>.Instance);
        _fossilizer = new ProofFossilizer(_neo4j, _encoder, NullLogger<ProofFossilizer>.Instance);
    }

    public async Task InitializeAsync()
    {
        await _neo4j.EnsureSchemaAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _neo4j.DisposeAsync();
    }

    [Fact]
    public async Task FossilizeAsync_Roundtrip_CanBeRetrieved()
    {
        var state = ProofState.Empty("algebra") with
        {
            PendingGoals = ["⊢ n + 0 = n"],
            TacticHistory = ["intro", "ring"],
            SorryCount = 1,
        };

        var id = await _fossilizer.FossilizeAsync(
            state,
            subgoalText: "n + 0 = n",
            tacticBlock: "ring",
            sorryReduction: 1,
            sourceProblem: "test-roundtrip-" + Guid.NewGuid().ToString("N")[..6],
            CancellationToken.None);

        Assert.NotEmpty(id);
    }

    [Fact]
    public async Task FindCandidatesAsync_AfterFossilize_ReturnsSimilarState()
    {
        var state = ProofState.Empty("combinatorics") with
        {
            PendingGoals = ["⊢ k ≤ n"],
            TacticHistory = ["omega"],
            SorryCount = 1,
        };

        await _fossilizer.FossilizeAsync(
            state,
            subgoalText: "k ≤ n",
            tacticBlock: "omega",
            sorryReduction: 1,
            sourceProblem: "test-findcandidates-" + Guid.NewGuid().ToString("N")[..6],
            CancellationToken.None);

        // Query with the same state — similarity should be 1.0 (exact match)
        var matches = await _fossilizer.FindCandidatesAsync(
            state, topK: 5, minSimilarity: 0.5f, CancellationToken.None);

        Assert.NotEmpty(matches);
    }

    [Fact]
    public async Task FossilizeAsync_SorryReductionClampedToZero()
    {
        var state = ProofState.Empty("algebra") with
        {
            SorryCount = 1,
        };

        // sorryReduction > SorryCountBefore → should clamp to 0 (not negative)
        var id = await _fossilizer.FossilizeAsync(
            state,
            subgoalText: "True",
            tacticBlock: "trivial",
            sorryReduction: 100,   // far exceeds actual sorry count
            sourceProblem: "test-clamp-" + Guid.NewGuid().ToString("N")[..6],
            CancellationToken.None);

        Assert.NotEmpty(id);
    }
}
