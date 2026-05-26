using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NexusAgent.Core.Agent;
using NexusAgent.Core.Configuration;
using NexusAgent.Core.Encoding;
using NexusAgent.Core.Llm;
using NexusAgent.Core.Memory;
using NexusAgent.Core.Models;
using NexusAgent.Core.Oracle;
using NexusAgent.Core.Planning;
using NexusAgent.Core.Prompts;
using NexusAgent.Core.Safety;

namespace NexusAgent.Tests.Agent;

/// <summary>
/// Phase 6: NexusProverSubagent — 4 tests (episode loop with mocked dependencies).
/// </summary>
public sealed class NexusProverSubagentTests
{
    private readonly Mock<ILeanOracle> _lean = new();
    private readonly Mock<ILlmClient> _qwen = new();
    private readonly Mock<ILlmClient> _flash = new();
    private readonly Mock<ILlmClient> _pro = new();
    private readonly Mock<INeo4jClient> _neo4j = new();
    private readonly NexusProverSubagent _agent;

    public NexusProverSubagentTests()
    {
        var config = Options.Create(new NexusConfig { TacticVocabPath = "does_not_exist.json" });
        var encoder = new ProofStateEncoder(config, NullLogger<ProofStateEncoder>.Instance);

        _qwen.SetupGet(c => c.Tier).Returns(LlmTier.Tier1_QwenLocal);
        _flash.SetupGet(c => c.Tier).Returns(LlmTier.Tier2_DeepSeekFlash);
        _pro.SetupGet(c => c.Tier).Returns(LlmTier.Tier3_PremiumCloud);

        _neo4j.Setup(n => n.NearestFossilsAsync(It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<float>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Array.Empty<FossilMatch>() as IReadOnlyList<FossilMatch>);
        _neo4j.Setup(n => n.NearbyLandmarksAsync(It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Array.Empty<ProofLandmark>() as IReadOnlyList<ProofLandmark>);
        _neo4j.Setup(n => n.UpsertLandmarkAsync(It.IsAny<ProofLandmark>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((ProofLandmark lm, CancellationToken _) => lm);
        _neo4j.Setup(n => n.RecordTransitionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TransitionOutcome>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);
        _neo4j.Setup(n => n.UpsertFossilAsync(It.IsAny<ProofFossil>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);

        var fossilizer = new ProofFossilizer(_neo4j.Object, encoder, NullLogger<ProofFossilizer>.Instance);
        var gate = new HallucinationGate(fossilizer, encoder, [_qwen.Object],
            NullLogger<HallucinationGate>.Instance);
        var cartographer = new ProofCartographer(_neo4j.Object, encoder,
            NullLogger<ProofCartographer>.Instance);
        var router = new TieredLlmRouter([_qwen.Object, _flash.Object, _pro.Object],
            new RouterConfig { BudgetCapUsd = 100m }, NullLogger<TieredLlmRouter>.Instance);
        var promptBuilder = new PromptBuilder();

        _agent = new NexusProverSubagent(
            _lean.Object, router, fossilizer, gate, cartographer, encoder,
            promptBuilder, NullLogger<NexusProverSubagent>.Instance);
    }

    private EpisodeContext MakeCtx(int maxTurns = 5) => new(
        ProblemId: "test-problem",
        ProblemStatement: "Prove 1 + 1 = 2",
        DomainTag: "algebra",
        InitialSketch: "theorem t : 1 + 1 = 2 := by sorry",
        EpisodeIndex: 0,
        EpisodeId: "ep0",
        MaxTurns: maxTurns,
        FossilMatchThreshold: 0.75f,
        FossilDirectSubstituteThreshold: 0.90f);

    private static LlmResponse MakeLlmResp(string content) => new()
    {
        Content = content,
        Tier = LlmTier.Tier1_QwenLocal,
        InputTokens = 100,
        OutputTokens = 50,
        CachedInputTokens = 0,
        EstimatedCostUsd = 0m,
        Latency = TimeSpan.FromMilliseconds(200),
    };

    private static LeanResult Solved => new()
    {
        Compiled = true,
        RemainingGoals = 0,
        SorryCount = 0,
        Errors = [],
        Warnings = [],
        CompileTime = TimeSpan.Zero,
        PendingGoalTexts = [],
    };

    private static LeanResult SorryResult(int sorry = 1) => new()
    {
        Compiled = true,
        RemainingGoals = sorry,
        SorryCount = sorry,
        Errors = [],
        Warnings = [],
        CompileTime = TimeSpan.Zero,
        PendingGoalTexts = sorry > 0 ? ["⊢ 1 + 1 = 2"] : [],
    };

    [Fact]
    public async Task RunEpisodeAsync_SolvedOnFirstCompile_ReturnsSolved()
    {
        // Initial compile already returns IsFullyProved=true
        _lean.Setup(l => l.CompileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(Solved);

        var result = await _agent.RunEpisodeAsync(MakeCtx(), CancellationToken.None);

        Assert.Equal(EpisodeOutcome.Solved, result.Outcome);
    }

    [Fact]
    public async Task RunEpisodeAsync_LlmProvidesSolution_ReturnsSolved()
    {
        var callCount = 0;
        _lean.Setup(l => l.CompileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(() => callCount++ == 0 ? SorryResult(1) : Solved);

        _qwen.Setup(c => c.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(MakeLlmResp("```lean\ntheorem t : 1 + 1 = 2 := by norm_num\n```"));

        var result = await _agent.RunEpisodeAsync(MakeCtx(), CancellationToken.None);

        Assert.Equal(EpisodeOutcome.Solved, result.Outcome);
    }

    [Fact]
    public async Task RunEpisodeAsync_MaxTurnsExhausted_ReturnsMaxTurns()
    {
        _lean.Setup(l => l.CompileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(SorryResult(1));

        _qwen.Setup(c => c.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(MakeLlmResp("```lean\ntheorem t : 1 + 1 = 2 := by sorry\n```"));

        var result = await _agent.RunEpisodeAsync(MakeCtx(maxTurns: 3), CancellationToken.None);

        Assert.Equal(EpisodeOutcome.MaxTurnsReached, result.Outcome);
    }

    [Fact]
    public async Task RunEpisodeAsync_ProgressRecorded_FossilCreated()
    {
        var callCount = 0;
        // Turn 0: compile initial → 2 sorrys. Turn 1: LLM sketch → 1 sorry (progress). Turn 2: → solved
        _lean.Setup(l => l.CompileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(() =>
             {
                 return callCount++ switch
                 {
                     0 => SorryResult(2),
                     1 => SorryResult(1),
                     _ => Solved,
                 };
             });

        _qwen.Setup(c => c.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(MakeLlmResp("```lean\ntheorem t : 1 + 1 = 2 := by norm_num\n```"));

        var result = await _agent.RunEpisodeAsync(MakeCtx(maxTurns: 10), CancellationToken.None);

        // At least one fossil should have been upserted during progress
        _neo4j.Verify(n => n.UpsertFossilAsync(It.IsAny<ProofFossil>(), It.IsAny<CancellationToken>()), Times.AtLeast(1));
    }
}
