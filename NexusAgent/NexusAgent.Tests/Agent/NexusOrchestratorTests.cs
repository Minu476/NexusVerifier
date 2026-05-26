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
/// Phase 7: NexusOrchestrator — 4 tests (full episode lifecycle management).
/// </summary>
public sealed class NexusOrchestratorTests
{
    private readonly Mock<ILeanOracle> _lean = new();
    private readonly Mock<ILlmClient> _qwen = new();
    private readonly Mock<ILlmClient> _flash = new();
    private readonly Mock<ILlmClient> _pro = new();
    private readonly Mock<INeo4jClient> _neo4j = new();
    private readonly NexusOrchestrator _orchestrator;
    private readonly TieredLlmRouter _router;

    public NexusOrchestratorTests()
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
        _neo4j.Setup(n => n.UpsertProblemAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);
        _neo4j.Setup(n => n.MarkProblemSolvedAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);

        var fossilizer = new ProofFossilizer(_neo4j.Object, encoder, NullLogger<ProofFossilizer>.Instance);
        var gate = new HallucinationGate(fossilizer, encoder, [_qwen.Object],
            NullLogger<HallucinationGate>.Instance);
        var cartographer = new ProofCartographer(_neo4j.Object, encoder,
            NullLogger<ProofCartographer>.Instance);
        _router = new TieredLlmRouter([_qwen.Object, _flash.Object, _pro.Object],
            new RouterConfig { BudgetCapUsd = 100m }, NullLogger<TieredLlmRouter>.Instance);
        var promptBuilder = new PromptBuilder();

        var subagent = new NexusProverSubagent(
            _lean.Object, _router, fossilizer, gate, cartographer, encoder,
            promptBuilder, NullLogger<NexusProverSubagent>.Instance);

        _orchestrator = new NexusOrchestrator(
            subagent, _lean.Object, _neo4j.Object, _router,
            NullLogger<NexusOrchestrator>.Instance);
    }

    private static ProblemInput MakeProblem() => new(
        Id: "OEIS-A000001",
        Source: "OEIS",
        DomainTag: "combinatorics",
        LeanFilePath: "/tmp/A000001.lean",
        Statement: "Prove n choose k ≥ 0",
        InitialSketch: "theorem t : ∀ n k : Nat, n.choose k ≥ 0 := by sorry");

    private static OrchestratorConfig QuickConfig(int maxEpisodes = 3) => new()
    {
        MaxEpisodes = maxEpisodes,
        MaxTurnsPerEpisode = 3,
        EpisodeTimeout = TimeSpan.FromSeconds(10),
        OverallTimeout = TimeSpan.FromMinutes(5),
    };

    private static LeanResult SolvedResult => new()
    {
        Compiled = true,
        RemainingGoals = 0,
        SorryCount = 0,
        Errors = [],
        Warnings = [],
        CompileTime = TimeSpan.Zero,
        PendingGoalTexts = [],
    };

    private static LeanResult SorryResult => new()
    {
        Compiled = true,
        RemainingGoals = 1,
        SorryCount = 1,
        Errors = [],
        Warnings = [],
        CompileTime = TimeSpan.Zero,
        PendingGoalTexts = ["⊢ n.choose k ≥ 0"],
    };

    [Fact]
    public async Task SolveAsync_InitialSketchAlreadySolved_ReturnsSolved()
    {
        _lean.Setup(l => l.CompileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(SolvedResult);

        var result = await _orchestrator.SolveAsync(MakeProblem(), QuickConfig(), CancellationToken.None);

        Assert.Equal(ProofOutcome.Solved, result.Outcome);
        Assert.Equal(1, result.EpisodesUsed);
        Assert.NotNull(result.FinalSketch);
    }

    [Fact]
    public async Task SolveAsync_LeanEnvironmentError_ReturnsLeanEnvironmentError()
    {
        _lean.Setup(l => l.CompileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(new LeanResult
             {
                 Compiled = false,
                 RemainingGoals = 0,
                 SorryCount = 0,
                 Errors = ["Lean environment not found"],
                 Warnings = [],
                 CompileTime = TimeSpan.Zero,
                 PendingGoalTexts = [],
             });

        var result = await _orchestrator.SolveAsync(MakeProblem(), QuickConfig(), CancellationToken.None);

        Assert.Equal(ProofOutcome.LeanEnvironmentError, result.Outcome);
        Assert.Equal(0, result.EpisodesUsed);
    }

    [Fact]
    public async Task SolveAsync_EpisodeBudgetExhausted_ReturnsExhausted()
    {
        // Always sorry → never solves → budget runs out
        _lean.Setup(l => l.CompileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(SorryResult);

        _qwen.Setup(c => c.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(new LlmResponse
             {
                 Content = "```lean\ntheorem t := by sorry\n```",
                 Tier = LlmTier.Tier1_QwenLocal,
                 InputTokens = 10, OutputTokens = 5,
                 CachedInputTokens = 0,
                 EstimatedCostUsd = 0m,
                 Latency = TimeSpan.FromMilliseconds(50),
             });

        var result = await _orchestrator.SolveAsync(MakeProblem(), QuickConfig(maxEpisodes: 2), CancellationToken.None);

        Assert.Equal(ProofOutcome.EpisodeBudgetExhausted, result.Outcome);
        Assert.Equal(2, result.EpisodesUsed);
    }

    [Fact]
    public async Task SolveAsync_MarksProblemSolvedInNeo4j()
    {
        _lean.Setup(l => l.CompileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(SolvedResult);

        await _orchestrator.SolveAsync(MakeProblem(), QuickConfig(), CancellationToken.None);

        _neo4j.Verify(n => n.MarkProblemSolvedAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
