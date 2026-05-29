using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NexusAgent.Core.Configuration;
using NexusAgent.Core.Encoding;
using NexusAgent.Core.Llm;
using NexusAgent.Core.Memory;
using NexusAgent.Core.Models;
using NexusAgent.Core.Safety;

namespace NexusAgent.Tests.Safety;

/// <summary>
/// Phase 4: HallucinationGate — 4 tests (two-layer fossil+LLM gate).
/// </summary>
public sealed class HallucinationGateTests
{
    private readonly Mock<INeo4jClient> _neo4j = new();
    private readonly Mock<ILlmClient> _qwen = new();
    private readonly HallucinationGate _gate;
    private readonly ProofStateEncoder _encoder;

    public HallucinationGateTests()
    {
        var config = Options.Create(new NexusConfig { TacticVocabPath = "does_not_exist.json" });
        _encoder = new ProofStateEncoder(config, NullLogger<ProofStateEncoder>.Instance);

        _neo4j.Setup(n => n.NearestFossilsAsync(It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<float>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Array.Empty<FossilMatch>() as IReadOnlyList<FossilMatch>);

        _qwen.SetupGet(c => c.Tier).Returns(LlmTier.Tier1_Cheap);

        var fossilizer = new ProofFossilizer(_neo4j.Object, _encoder, NullLogger<ProofFossilizer>.Instance);
        _gate = new HallucinationGate(fossilizer, _encoder, [_qwen.Object],
            NullLogger<HallucinationGate>.Instance);
    }

    [Fact]
    public async Task ScanAsync_NoLemmas_ReturnsEmptyWarnings()
    {
        var sketch = """
            theorem trivial_ok : True := by trivial
            """;

        var warnings = await _gate.ScanAsync(sketch, "algebra", CancellationToken.None);

        Assert.Empty(warnings);
        _qwen.Verify(c => c.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ScanAsync_SorryLemma_QwenClassifiesReal_NoWarning()
    {
        _qwen.Setup(c => c.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(new LlmResponse
             {
                 Content = "REAL",
                 Tier = LlmTier.Tier1_Cheap,
                 InputTokens = 50, OutputTokens = 1,
                 CachedInputTokens = 0,
                 EstimatedCostUsd = 0m,
                 Latency = TimeSpan.FromMilliseconds(100),
             });

        var sketch = """
            lemma helper_lemma : 1 + 1 = 2 := by sorry
            theorem main : 1 + 1 = 2 := helper_lemma
            """;

        var warnings = await _gate.ScanAsync(sketch, "algebra", CancellationToken.None);

        Assert.Empty(warnings);
    }

    [Fact]
    public async Task ScanAsync_SorryLemma_QwenClassifiesSuspect_ReturnsWarning()
    {
        _qwen.Setup(c => c.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(new LlmResponse
             {
                 Content = "SUSPECT",
                 Tier = LlmTier.Tier1_Cheap,
                 InputTokens = 50, OutputTokens = 1,
                 CachedInputTokens = 0,
                 EstimatedCostUsd = 0m,
                 Latency = TimeSpan.FromMilliseconds(100),
             });

        var sketch = """
            lemma fake_lemma : 2 + 2 = 5 := by sorry
            theorem main : 2 + 2 = 5 := fake_lemma
            """;

        var warnings = await _gate.ScanAsync(sketch, "algebra", CancellationToken.None);

        Assert.NotEmpty(warnings);
        Assert.Contains(warnings, w => w.LemmaName == "fake_lemma");
    }

    [Fact]
    public async Task ScanAsync_FossilCorroborates_SkipsQwen()
    {
        // Fossil vault has a high-similarity match → trust it, skip Qwen
        var fossilMatch = new FossilMatch(
            new ProofFossil
            {
                Id = Guid.NewGuid().ToString("N"),
                SubgoalText = "1 + 1 = 2",
                TacticBlock = "norm_num",
                StateVector = new float[64],
                DomainTag = "algebra",
                SorryCountBefore = 1,
                SorryCountAfter = 0,
                ProvedAt = DateTime.UtcNow,
                SourceProblems = ["test"],
                UseCount = 0,
            },
            Similarity: 0.95f);

        _neo4j.Setup(n => n.NearestFossilsAsync(It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<float>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new[] { fossilMatch } as IReadOnlyList<FossilMatch>);

        var sketch = """
            lemma corroborated : 1 + 1 = 2 := by sorry
            theorem main : 1 + 1 = 2 := corroborated
            """;

        var warnings = await _gate.ScanAsync(sketch, "algebra", CancellationToken.None);

        Assert.Empty(warnings);
        _qwen.Verify(c => c.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
