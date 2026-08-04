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
    private readonly Mock<IToposTacticStore> _toposStore = new();
    private readonly NexusOrchestrator _orchestrator;
    private readonly TieredLlmRouter _router;

    public NexusOrchestratorTests()
    {
        var config = Options.Create(new NexusConfig { TacticVocabPath = "does_not_exist.json" });
        var encoder = new ProofStateEncoder(config, NullLogger<ProofStateEncoder>.Instance);

        _toposStore.Setup(t => t.ProposeAsync(It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Array.Empty<GraphTacticProposal>() as IReadOnlyList<GraphTacticProposal>);

        _qwen.SetupGet(c => c.Tier).Returns(LlmTier.Tier1_Cheap);
        _flash.SetupGet(c => c.Tier).Returns(LlmTier.Tier2_DeepSeekFlash);
        _pro.SetupGet(c => c.Tier).Returns(LlmTier.Tier3_PremiumCloud);

        _neo4j.Setup(n => n.NearestFossilsAsync(It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<float>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Array.Empty<FossilMatch>() as IReadOnlyList<FossilMatch>);
        _neo4j.Setup(n => n.NearbyLandmarksAsync(It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Array.Empty<ProofLandmark>() as IReadOnlyList<ProofLandmark>);
        _neo4j.Setup(n => n.NearbySolvedLandmarksAsync(It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Array.Empty<ProofLandmark>() as IReadOnlyList<ProofLandmark>);
        _neo4j.Setup(n => n.ShortestSuccessfulPathAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((IReadOnlyList<string>?)null);
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
            _lean.Object, _router, fossilizer, gate, cartographer, _neo4j.Object, _toposStore.Object,
            encoder, promptBuilder, NullLogger<NexusProverSubagent>.Instance);
        var planner = new BestFirstGraphPlanner(
            _neo4j.Object,
            _lean.Object,
            encoder,
            NullLogger<BestFirstGraphPlanner>.Instance);

        _orchestrator = new NexusOrchestrator(
            subagent, planner, _lean.Object, _neo4j.Object, _router, new PromptBuilder(),
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

    // ── Task 3 (docs/SESSION_HANDOFF_2026-08-03.md): citation-exploit gate, end-to-end ──

    [Fact]
    public async Task SolveAsync_WinningProofIsCitationOfOriginal_IsNotSolved_AndSearchContinues()
    {
        // Contract CHANGED 2026-08-04 (citation-keeps-searching, per Opus's experiment review).
        // Previously a detected citation returned ProofOutcome.CitationExploit immediately. That
        // made the gate a *reporting* gate rather than a *search* gate: the episode ended before
        // any stuck condition, so citation-prone problems could never reach the reformulation
        // path — they were dead weight in both arms of the A/B, and a null result was expected
        // by construction. Now a citation is treated as not-solved and the search continues, so
        // the terminal outcome is whatever the continued search yields (here: the episode budget
        // runs out), NOT CitationExploit.
        //
        // The two properties that must still hold are asserted below; the specific terminal
        // enum value is deliberately not pinned, because it legitimately depends on what the
        // continued search finds.
        _lean.Setup(l => l.CompileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(SolvedResult);

        var problem = MakeProblem() with
        {
            InitialSketch = "theorem t : ∀ n k : Nat, n.choose k ≥ 0 := by exact original_lemma",
            OriginalDeclarationBareName = "original_lemma",
        };

        var result = await _orchestrator.SolveAsync(problem, QuickConfig(), CancellationToken.None);

        // 1) A citation is never reported as a genuine solve.
        Assert.NotEqual(ProofOutcome.Solved, result.Outcome);
        // 2) A citation must not poison Neo4j's "already solved" state — a spurious solved flag
        //    would make Program.cs's already-solved check permanently SKIP this problem on every
        //    future run, silently shrinking the corpus (this exact hazard was found live in the
        //    round-1 A/B data: 12 problems marked solved by citation).
        _neo4j.Verify(n => n.MarkProblemSolvedAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SolveAsync_IndependentProof_StillReturnsSolved_NotOverRejected()
    {
        // The boundary the handoff calls out: knowing the original declaration's name must
        // not turn every solve into a rejection. A proof that does not cite that specific
        // declaration is still a real solve, even though OriginalDeclarationBareName is set.
        _lean.Setup(l => l.CompileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(SolvedResult);

        var problem = MakeProblem() with
        {
            InitialSketch = "theorem t : ∀ n k : Nat, n.choose k ≥ 0 := by exact Nat.zero_le _",
            OriginalDeclarationBareName = "original_lemma",
        };

        var result = await _orchestrator.SolveAsync(problem, QuickConfig(), CancellationToken.None);

        Assert.Equal(ProofOutcome.Solved, result.Outcome);
        _neo4j.Verify(n => n.MarkProblemSolvedAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once);
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
                 Tier = LlmTier.Tier1_Cheap,
                 InputTokens = 10, OutputTokens = 5,
                 CachedInputTokens = 0,
                 EstimatedCostUsd = 0m,
                 Latency = TimeSpan.FromMilliseconds(50),
             });

        var result = await _orchestrator.SolveAsync(MakeProblem(), QuickConfig(maxEpisodes: 1), CancellationToken.None);

        Assert.Equal(ProofOutcome.EpisodeBudgetExhausted, result.Outcome);
        Assert.Equal(1, result.EpisodesUsed);
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
