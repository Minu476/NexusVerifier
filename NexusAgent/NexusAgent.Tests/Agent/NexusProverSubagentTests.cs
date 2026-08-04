using System.Reflection;
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
/// Phase 6: NexusProverSubagent — 5 tests (episode loop with mocked dependencies).
/// </summary>
public sealed class NexusProverSubagentTests
{
    private readonly Mock<ILeanOracle> _lean = new();
    private readonly Mock<ILlmClient> _qwen = new();
    private readonly Mock<ILlmClient> _flash = new();
    private readonly Mock<ILlmClient> _pro = new();
    private readonly Mock<INeo4jClient> _neo4j = new();
    private readonly Mock<IToposTacticStore> _toposStore = new();
    private readonly NexusProverSubagent _agent;

    public NexusProverSubagentTests()
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

        var fossilizer = new ProofFossilizer(_neo4j.Object, encoder, NullLogger<ProofFossilizer>.Instance);
        var gate = new HallucinationGate(fossilizer, encoder, [_qwen.Object],
            NullLogger<HallucinationGate>.Instance);
        var cartographer = new ProofCartographer(_neo4j.Object, encoder,
            NullLogger<ProofCartographer>.Instance);
        var router = new TieredLlmRouter([_qwen.Object, _flash.Object, _pro.Object],
            new RouterConfig { BudgetCapUsd = 100m }, NullLogger<TieredLlmRouter>.Instance);
        var promptBuilder = new PromptBuilder();

        _agent = new NexusProverSubagent(
            _lean.Object, router, fossilizer, gate, cartographer, _neo4j.Object, _toposStore.Object,
            encoder, promptBuilder, NullLogger<NexusProverSubagent>.Instance);
    }

    private EpisodeContext MakeCtx(int maxTurns = 5, ProofGoalGraph? goalGraph = null,
        bool pivotGatesEnabled = true, int maxDiagnosesPerEpisode = 1) => new(
        ProblemId: "test-problem",
        ProblemStatement: "Prove 1 + 1 = 2",
        DomainTag: "algebra",
        InitialSketch: "theorem target_main : 1 + 1 = 2 := by sorry",
        EpisodeIndex: 0,
        EpisodeId: "ep0",
        MaxTurns: maxTurns,
        FossilMatchThreshold: 0.75f,
        FossilDirectSubstituteThreshold: 0.90f,
        GoalGraph: goalGraph ?? new ProofGoalGraph(),
        PivotGatesEnabled: pivotGatesEnabled,
        MaxDiagnosesPerEpisode: maxDiagnosesPerEpisode);

    private static LlmResponse MakeLlmResp(string content) => new()
    {
        Content = content,
        Tier = LlmTier.Tier1_Cheap,
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
               .ReturnsAsync(MakeLlmResp("```lean\ntheorem target_main : 1 + 1 = 2 := by norm_num\n```"));

        var result = await _agent.RunEpisodeAsync(MakeCtx(), CancellationToken.None);

        Assert.Equal(EpisodeOutcome.Solved, result.Outcome);
    }

    [Fact]
    public async Task RunEpisodeAsync_MaxTurnsExhausted_ReturnsMaxTurns()
    {
        _lean.Setup(l => l.CompileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(SorryResult(1));

        _qwen.Setup(c => c.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(MakeLlmResp("```lean\ntheorem target_main : 1 + 1 = 2 := by sorry\n```"));

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
               .ReturnsAsync(MakeLlmResp("```lean\ntheorem target_main : 1 + 1 = 2 := by norm_num\n```"));

        var result = await _agent.RunEpisodeAsync(MakeCtx(maxTurns: 10), CancellationToken.None);

        // At least one fossil should have been upserted during progress
        _neo4j.Verify(n => n.UpsertFossilAsync(It.IsAny<ProofFossil>(), It.IsAny<CancellationToken>()), Times.AtLeast(1));
    }

    [Fact]
    public async Task RunEpisodeAsync_RepeatedStructuralViolations_ReturnsStructuralGateRejection()
    {
        _lean.Setup(l => l.CompileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(SorryResult(1));

        _qwen.Setup(c => c.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
                         .ReturnsAsync(MakeLlmResp("```lean\ntheorem hacked : 1 + 1 = 2 := by sorry\n```"));
          _flash.Setup(c => c.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
                            .ReturnsAsync(MakeLlmResp("```lean\ntheorem hacked : 1 + 1 = 2 := by sorry\n```"));
        _pro.Setup(c => c.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
                        .ReturnsAsync(MakeLlmResp("```lean\ntheorem hacked : 1 + 1 = 2 := by sorry\n```"));

        var result = await _agent.RunEpisodeAsync(MakeCtx(maxTurns: 8), CancellationToken.None);

        Assert.Equal(EpisodeOutcome.StructuralGateRejection, result.Outcome);
        // After the obstruction-diagnosis gate (2026-08-03), the 2nd violation triggers a
        // diagnosis call instead of immediate abort. The diagnosis (mocked to return a hacked
        // sketch too) doesn't help, so a 3rd violation aborts. Allow up to 4 turns for the
        // diagnosis round-trip + the post-diagnosis violation.
        Assert.True(result.TurnsUsed <= 4);
    }

    [Theory]
    [InlineData(0)]  // diagnosis gate never fires — behaves like PivotGatesEnabled=false
    [InlineData(1)]  // default cap
    [InlineData(3)]  // a generous cap
    public async Task RunEpisodeAsync_AlwaysStructuralViolation_TerminatesRegardlessOfDiagnosisCap(
        int maxDiagnosesPerEpisode)
    {
        // Safety-cap regression test (2026-08-03): a model that always reward-hacks (renames the
        // theorem) must never loop cheat -> diagnose -> cheat -> diagnose forever. Each diagnosis
        // resets the consecutive-violation counter, so without a hard cap on diagnoses-per-episode
        // the episode could in principle keep resetting until MaxTurns. Prove termination holds for
        // several cap values, not just the default, since the cap is now a config knob
        // (OrchestratorConfig.MaxDiagnosesPerEpisode / EpisodeContext.MaxDiagnosesPerEpisode).
        _lean.Setup(l => l.CompileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(SorryResult(1));

        var alwaysHacked = MakeLlmResp("```lean\ntheorem hacked : 1 + 1 = 2 := by sorry\n```");
        _qwen.Setup(c => c.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(alwaysHacked);
        _flash.Setup(c => c.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(alwaysHacked);
        _pro.Setup(c => c.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(alwaysHacked);

        // Generous turn budget: if the cap did NOT bound the loop, this would run out the clock
        // at MaxTurnsReached instead of aborting early via StructuralGateRejection.
        const int maxTurns = 50;
        var result = await _agent.RunEpisodeAsync(
            MakeCtx(maxTurns: maxTurns, maxDiagnosesPerEpisode: maxDiagnosesPerEpisode),
            CancellationToken.None);

        Assert.Equal(EpisodeOutcome.StructuralGateRejection, result.Outcome);
        // Each diagnosis buys at most 2 extra consecutive-violation turns before the counter
        // maxes out again; with `maxDiagnosesPerEpisode` diagnoses allowed, the episode must
        // abort well before MaxTurns regardless of the cap's value.
        var upperBound = 2 * (maxDiagnosesPerEpisode + 1) + 1;
        Assert.True(result.TurnsUsed <= upperBound,
            $"episode used {result.TurnsUsed} turns, expected to abort within {upperBound} " +
            $"(maxDiagnosesPerEpisode={maxDiagnosesPerEpisode})");
        Assert.True(result.TurnsUsed < maxTurns,
            "episode ran to MaxTurnsReached instead of terminating via the diagnosis cap");
    }

    [Fact]
    public async Task RunEpisodeAsync_NonProgressingAttempt_ThenNextPromptIncludesGoalHistory()
    {
        // Guards the graph-native proof-state milestone's core payoff: a tactic
        // that didn't help on a goal should be visible to the LLM on the *next*
        // turn's prompt, so it isn't blindly repeated.
        _lean.Setup(l => l.CompileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(SorryResult(1));

        var capturedRequests = new List<LlmRequest>();
        _qwen.Setup(c => c.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
             .Returns((LlmRequest req, CancellationToken _) =>
             {
                 capturedRequests.Add(req);
                 return Task.FromResult(MakeLlmResp(
                     "```lean\ntheorem target_main : 1 + 1 = 2 := by\n  bad_tactic\n  sorry\n```"));
             });

        await _agent.RunEpisodeAsync(MakeCtx(maxTurns: 3), CancellationToken.None);

        Assert.True(capturedRequests.Count >= 2,
            $"expected at least 2 LLM turns to compare, got {capturedRequests.Count}");
        var laterPrompt = capturedRequests[^1].Messages.Single(m => m.Role == "user").Content;
        Assert.Contains("Goal history", laterPrompt);
        Assert.Contains("bad_tactic", laterPrompt);
    }

    [Fact]
    public void SubstituteFirstSorry_Unchanged_ByteForByte()
    {
        // Guards the "don't touch this" constraint from the graph-native
        // proof-state milestone: the goal-graph enriches the prompt, it must
        // never change which text position SubstituteFirstSorry targets or how
        // it splices — direct reflection test of the private static method
        // rather than an indirect assertion through the whole episode loop.
        var method = typeof(NexusProverSubagent).GetMethod(
            "SubstituteFirstSorry", BindingFlags.NonPublic | BindingFlags.Static)!;

        var input = "theorem foo : True := by\n  sorry";
        var result = (string)method.Invoke(null, [input, "trivial"])!;

        Assert.Equal("theorem foo : True := by\n  trivial", result);
    }
}
