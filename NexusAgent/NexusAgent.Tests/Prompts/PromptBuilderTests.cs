using NexusAgent.Core.Prompts;

namespace NexusAgent.Tests.Prompts;

/// <summary>
/// Regression coverage for the reformulation-retry error-feedback bug found in the
/// 2026-08-03 pivot-gate A/B: the orchestrator's log claimed a failed reformulation attempt
/// was "retrying with error feedback", but both attempts called BuildReformulationRequest
/// with an identical prompt — the compile error never actually reached the model. These
/// tests pin the fix: the prior sketch + errors, when supplied, must appear verbatim in the
/// built prompt, and must be absent on a first attempt (no prior data).
/// </summary>
public sealed class PromptBuilderTests
{
    private readonly PromptBuilder _builder = new();

    private static string UserContent(NexusAgent.Core.Llm.LlmRequest req) =>
        req.Messages.Single(m => m.Role == "user").Content;

    [Fact]
    public void BuildReformulationRequest_NoPriorAttempt_OmitsErrorFeedbackSection()
    {
        var req = _builder.BuildReformulationRequest(
            problemStatement: "Prove 1 + 1 = 2",
            bestSketch: "theorem t : 1 + 1 = 2 := by sorry",
            bestSorryCount: 1,
            episodesAttempted: 2);

        var content = UserContent(req);

        Assert.DoesNotContain("did not compile", content);
        Assert.DoesNotContain("Compiler errors:", content);
    }

    [Fact]
    public void BuildReformulationRequest_WithPriorAttempt_IncludesSketchAndErrorsVerbatim()
    {
        const string priorSketch = "theorem t : 1 + 1 = 2 := by induction_failure_attempt";
        string[] priorErrors = ["error: unknown tactic 'induction_failure_attempt'"];

        var req = _builder.BuildReformulationRequest(
            problemStatement: "Prove 1 + 1 = 2",
            bestSketch: "theorem t : 1 + 1 = 2 := by sorry",
            bestSorryCount: 1,
            episodesAttempted: 2,
            priorAttemptSketch: priorSketch,
            priorAttemptErrors: priorErrors);

        var content = UserContent(req);

        Assert.Contains("did not compile", content);
        Assert.Contains(priorSketch, content);
        Assert.Contains(priorErrors[0], content);
    }

    [Fact]
    public void BuildReformulationRequest_WithPriorAttempt_UsesDistinctCacheKeyFromFirstAttempt()
    {
        var first = _builder.BuildReformulationRequest(
            "Prove 1 + 1 = 2", "theorem t : 1 + 1 = 2 := by sorry", 1, episodesAttempted: 2);

        var retry = _builder.BuildReformulationRequest(
            "Prove 1 + 1 = 2", "theorem t : 1 + 1 = 2 := by sorry", 1, episodesAttempted: 2,
            priorAttemptSketch: "theorem t : 1 + 1 = 2 := by bad_tactic",
            priorAttemptErrors: ["error: unknown tactic"]);

        Assert.NotEqual(first.CacheKey, retry.CacheKey);
    }

    [Fact]
    public void BuildReformulationRequest_EmptyPriorErrors_TreatedAsNoPriorAttempt()
    {
        // Guards the `IReadOnlyList<string>? priorAttemptErrors is { Count: > 0 }` check —
        // an empty (not null) array must not trigger the error-feedback section.
        var req = _builder.BuildReformulationRequest(
            problemStatement: "Prove 1 + 1 = 2",
            bestSketch: "theorem t : 1 + 1 = 2 := by sorry",
            bestSorryCount: 1,
            episodesAttempted: 2,
            priorAttemptSketch: "theorem t : 1 + 1 = 2 := by bad_tactic",
            priorAttemptErrors: []);

        Assert.DoesNotContain("did not compile", UserContent(req));
    }
}
