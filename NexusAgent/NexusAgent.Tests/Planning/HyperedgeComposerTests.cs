using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NexusAgent.Core.Memory;
using NexusAgent.Core.Planning;

namespace NexusAgent.Tests.Planning;

/// <summary>
/// Unit tests for <see cref="HyperedgeComposer"/>.
/// Uses a mock <see cref="INeo4jClient"/> — no real Neo4j connection required.
/// </summary>
public sealed class HyperedgeComposerTests
{
    private static HyperedgeRecord MakeEdge(string lemmaName, string output, params string[] inputs) =>
        new()
        {
            Id         = $"test-{lemmaName}",
            LemmaName  = lemmaName,
            Output     = output,
            OutputHash = 0,
            Inputs     = inputs,
            BuiltAt    = DateTime.UtcNow,
            SeedRun    = "test",
        };

    // ── ExtractConclusion ─────────────────────────────────────────────────────

    [Fact]
    public void ExtractConclusion_BareGoalSymbol_ReturnsConclusionText()
    {
        var text = "⊢ n + m = m + n";
        Assert.Equal("n + m = m + n", HyperedgeComposer.ExtractConclusion(text));
    }

    [Fact]
    public void ExtractConclusion_WithHypotheses_ReturnsLastLine()
    {
        var text = "a b : ℕ\nhab : a ∣ b\n⊢ a ∣ c";
        Assert.Equal("a ∣ c", HyperedgeComposer.ExtractConclusion(text));
    }

    [Fact]
    public void ExtractConclusion_NoTurnstile_ReturnsTrimmedText()
    {
        var text = "  a ∣ c  ";
        Assert.Equal("a ∣ c", HyperedgeComposer.ExtractConclusion(text));
    }

    // ── BuildTacticSketch ─────────────────────────────────────────────────────

    [Fact]
    public void BuildTacticSketch_LeafNode_ReturnsExactLemma()
    {
        var node = new DerivationNode
        {
            Edge               = MakeEdge("Nat.add_comm", "n + m = m + n"),
            PremiseDerivations = [],
        };
        Assert.Equal("exact Nat.add_comm", HyperedgeComposer.BuildTacticSketch(node));
    }

    [Fact]
    public void BuildTacticSketch_OnePremise_ReturnsFlatApplication()
    {
        var premise = new DerivationNode
        {
            Edge               = MakeEdge("dvd_refl", "a ∣ a"),
            PremiseDerivations = [],
        };
        var root = new DerivationNode
        {
            Edge               = MakeEdge("dvd_trans", "a ∣ c", "a ∣ b", "b ∣ c"),
            PremiseDerivations = [premise, premise],
        };
        Assert.Equal("exact dvd_trans dvd_refl dvd_refl", HyperedgeComposer.BuildTacticSketch(root));
    }

    [Fact]
    public void BuildTacticSketch_NestedPremise_WrapsCompoundTermInParens()
    {
        // depth-2: root uses `outer` which takes `inner lemA lemB`
        var innerLeaf1 = new DerivationNode
        {
            Edge               = MakeEdge("lemA", "P"),
            PremiseDerivations = [],
        };
        var innerLeaf2 = new DerivationNode
        {
            Edge               = MakeEdge("lemB", "Q"),
            PremiseDerivations = [],
        };
        var innerNode = new DerivationNode
        {
            Edge               = MakeEdge("inner", "R", "P", "Q"),
            PremiseDerivations = [innerLeaf1, innerLeaf2],
        };
        var root = new DerivationNode
        {
            Edge               = MakeEdge("outer", "S", "R"),
            PremiseDerivations = [innerNode],
        };
        Assert.Equal("exact outer (inner lemA lemB)", HyperedgeComposer.BuildTacticSketch(root));
    }

    // ── TryComposeAsync — leaf ────────────────────────────────────────────────

    [Fact]
    public async Task TryComposeAsync_LeafEdgeExistsForGoal_ReturnsDerivation()
    {
        var neo4j = new Mock<INeo4jClient>();
        neo4j.Setup(n => n.GetHyperedgesByOutputAsync("n + m = m + n", It.IsAny<CancellationToken>()))
             .ReturnsAsync([MakeEdge("Nat.add_comm", "n + m = m + n")]);

        var composer = new HyperedgeComposer(neo4j.Object, NullLogger<HyperedgeComposer>.Instance);
        var result = await composer.TryComposeAsync("⊢ n + m = m + n", maxDepth: 2, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.IsLeaf);
        Assert.Equal("Nat.add_comm", result.Edge.LemmaName);
        Assert.Equal("exact Nat.add_comm", HyperedgeComposer.BuildTacticSketch(result));
    }

    [Fact]
    public async Task TryComposeAsync_NoEdgeInStore_ReturnsNull()
    {
        var neo4j = new Mock<INeo4jClient>();
        neo4j.Setup(n => n.GetHyperedgesByOutputAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((IReadOnlyList<HyperedgeRecord>)[]);

        var composer = new HyperedgeComposer(neo4j.Object, NullLogger<HyperedgeComposer>.Instance);
        var result = await composer.TryComposeAsync("⊢ unsolvable goal", maxDepth: 2, CancellationToken.None);

        Assert.Null(result);
    }

    // ── TryComposeAsync — AND-join ────────────────────────────────────────────

    [Fact]
    public async Task TryComposeAsync_TwoPremisesAllSolvable_ReturnsDerivation()
    {
        var neo4j = new Mock<INeo4jClient>();

        // Root edge: dvd_trans : (a ∣ b) → (b ∣ c) → (a ∣ c)
        neo4j.Setup(n => n.GetHyperedgesByOutputAsync("a ∣ c", It.IsAny<CancellationToken>()))
             .ReturnsAsync([MakeEdge("dvd_trans", "a ∣ c", "a ∣ b", "b ∣ c")]);

        // Premise edges (leaves)
        neo4j.Setup(n => n.GetHyperedgesByOutputAsync("a ∣ b", It.IsAny<CancellationToken>()))
             .ReturnsAsync([MakeEdge("hab", "a ∣ b")]);
        neo4j.Setup(n => n.GetHyperedgesByOutputAsync("b ∣ c", It.IsAny<CancellationToken>()))
             .ReturnsAsync([MakeEdge("hbc", "b ∣ c")]);

        var composer = new HyperedgeComposer(neo4j.Object, NullLogger<HyperedgeComposer>.Instance);
        var result = await composer.TryComposeAsync("a ∣ b\n⊢ a ∣ c", maxDepth: 2, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, result.Depth);
        Assert.Equal("dvd_trans", result.Edge.LemmaName);
        Assert.Equal(2, result.PremiseDerivations.Count);
        Assert.Equal("exact dvd_trans hab hbc", HyperedgeComposer.BuildTacticSketch(result));
    }

    [Fact]
    public async Task TryComposeAsync_OnePremiseUnsolvable_ReturnsNull()
    {
        var neo4j = new Mock<INeo4jClient>();

        neo4j.Setup(n => n.GetHyperedgesByOutputAsync("a ∣ c", It.IsAny<CancellationToken>()))
             .ReturnsAsync([MakeEdge("dvd_trans", "a ∣ c", "a ∣ b", "b ∣ c")]);

        // First premise is solvable, second is not.
        neo4j.Setup(n => n.GetHyperedgesByOutputAsync("a ∣ b", It.IsAny<CancellationToken>()))
             .ReturnsAsync([MakeEdge("hab", "a ∣ b")]);
        neo4j.Setup(n => n.GetHyperedgesByOutputAsync("b ∣ c", It.IsAny<CancellationToken>()))
             .ReturnsAsync((IReadOnlyList<HyperedgeRecord>)[]);

        var composer = new HyperedgeComposer(neo4j.Object, NullLogger<HyperedgeComposer>.Instance);
        var result = await composer.TryComposeAsync("⊢ a ∣ c", maxDepth: 2, CancellationToken.None);

        Assert.Null(result);
    }

    // ── TryComposeAsync — depth limit ─────────────────────────────────────────

    [Fact]
    public async Task TryComposeAsync_NonLeafAtMaxDepthZero_ReturnsNull()
    {
        var neo4j = new Mock<INeo4jClient>();
        // Only a non-leaf edge available.
        neo4j.Setup(n => n.GetHyperedgesByOutputAsync("goal", It.IsAny<CancellationToken>()))
             .ReturnsAsync([MakeEdge("lemma", "goal", "premise")]);
        neo4j.Setup(n => n.GetHyperedgesByOutputAsync("premise", It.IsAny<CancellationToken>()))
             .ReturnsAsync([MakeEdge("lem2", "premise")]);

        var composer = new HyperedgeComposer(neo4j.Object, NullLogger<HyperedgeComposer>.Instance);
        // maxDepth 0 means only leaves are allowed.
        var result = await composer.TryComposeAsync("⊢ goal", maxDepth: 0, CancellationToken.None);

        Assert.Null(result);
    }

    // ── TryComposeAsync — cycle guard ─────────────────────────────────────────

    [Fact]
    public async Task TryComposeAsync_CyclicPremise_ReturnsNullWithoutInfiniteLoop()
    {
        var neo4j = new Mock<INeo4jClient>();
        // A → B → A cycle
        neo4j.Setup(n => n.GetHyperedgesByOutputAsync("A", It.IsAny<CancellationToken>()))
             .ReturnsAsync([MakeEdge("lemAB", "A", "B")]);
        neo4j.Setup(n => n.GetHyperedgesByOutputAsync("B", It.IsAny<CancellationToken>()))
             .ReturnsAsync([MakeEdge("lemBA", "B", "A")]);

        var composer = new HyperedgeComposer(neo4j.Object, NullLogger<HyperedgeComposer>.Instance);
        var result = await composer.TryComposeAsync("⊢ A", maxDepth: 5, CancellationToken.None);

        // Must not hang and must return null (no acyclic derivation exists).
        Assert.Null(result);
    }
}
