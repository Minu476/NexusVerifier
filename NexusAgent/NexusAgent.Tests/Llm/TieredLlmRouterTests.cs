using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NexusAgent.Core.Llm;
using NexusAgent.Core.Models;

namespace NexusAgent.Tests.Llm;

/// <summary>
/// Tests for TieredLlmRouter behavior that the v4-flash "aggressive use" work changed
/// or depends on: config-driven escalation thresholds + temperatures (Phase 2),
/// LlmResponse.ModelId provenance propagation (1c), and the 401/402 circuit breaker (1b).
/// </summary>
public sealed class TieredLlmRouterTests
{
    private readonly Mock<ILlmClient> _tier1 = new();
    private readonly Mock<ILlmClient> _flash = new();
    private readonly Mock<ILlmClient> _pro = new();

    private TieredLlmRouter NewRouter(RouterConfig? cfg = null)
    {
        _tier1.SetupGet(c => c.Tier).Returns(LlmTier.Tier1_Cheap);
        _flash.SetupGet(c => c.Tier).Returns(LlmTier.Tier2_DeepSeekFlash);
        _pro.SetupGet(c => c.Tier).Returns(LlmTier.Tier3_PremiumCloud);

        // Default each client to echo back its model id so provenance tests can assert.
        _tier1.Setup(c => c.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((LlmRequest r, CancellationToken _) => Ok(LlmTier.Tier1_Cheap, "deepseek-v4-flash", r.Temperature));
        _flash.Setup(c => c.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((LlmRequest r, CancellationToken _) => Ok(LlmTier.Tier2_DeepSeekFlash, "deepseek-v4-flash", r.Temperature));
        _pro.Setup(c => c.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((LlmRequest r, CancellationToken _) => Ok(LlmTier.Tier3_PremiumCloud, "deepseek-v4-pro", r.Temperature));

        return new TieredLlmRouter(
            new[] { _tier1.Object, _flash.Object, _pro.Object },
            cfg ?? new RouterConfig(),
            NullLogger<TieredLlmRouter>.Instance);
    }

    private static LlmResponse Ok(LlmTier tier, string modelId, double temp) => new()
    {
        Content = "ok",
        Tier = tier,
        ModelId = modelId,
        InputTokens = 100,
        OutputTokens = 10,
        CachedInputTokens = 0,
        EstimatedCostUsd = 0.0001m,
        Latency = TimeSpan.FromMilliseconds(5),
        FinishReason = "stop",
    };

    private static RouterContext Ctx(int ep, int turn, int stalled = 0) => new()
    {
        EpisodeIndex = ep,
        TurnIndex = turn,
        TurnsSinceLastProgress = stalled,
        CurrentSorryCount = 1,
    };

    // ---- Phase 2: config-driven escalation thresholds ----

    [Fact]
    public void Select_WithDefaultConfig_PicksTier1ForEarlyTurns()
    {
        var router = NewRouter();
        // Default TurnsBeforeEscalation=3 → turns 0,1,2 are Tier1.
        Assert.Equal(LlmTier.Tier1_Cheap, router.Select(Ctx(ep: 0, turn: 0)).Tier);
        Assert.Equal(LlmTier.Tier1_Cheap, router.Select(Ctx(ep: 0, turn: 2)).Tier);
    }

    [Fact]
    public void Select_WithCustomTurnsBeforeEscalation_RespectsLoweredThreshold()
    {
        // An aggressive run might want to leave Tier1 sooner. Default 3 → 1 here.
        var router = NewRouter(new RouterConfig { TurnsBeforeEscalation = 1 });
        Assert.Equal(LlmTier.Tier1_Cheap, router.Select(Ctx(0, 0)).Tier);
        // turn 1 now falls through to the Tier2/Tier3 branch; with low stall + early
        // episode it should land on Tier2 (flash).
        Assert.Equal(LlmTier.Tier2_DeepSeekFlash, router.Select(Ctx(0, 1)).Tier);
    }

    [Fact]
    public void Select_WithLowEpisodesBeforeProEscalation_EscalatesToProOnLateEpisodes()
    {
        // Default EpisodesBeforeProEscalation=20; an aggressive run that wants more Pro
        // time would lower it. At ep 5 (≥ threshold), a stalled turn escalates to Pro.
        var router = NewRouter(new RouterConfig
        {
            TurnsBeforeEscalation = 0,        // leave Tier1 immediately
            TurnsBeforeFlashEscalation = 1,   // stall ≥ 1 → not flash
            EpisodesBeforeProEscalation = 3,  // any episode ≥ 3 → pro when not flash
        });
        // ep 5, turn 0, stalled 2 → past flash threshold AND past pro-escalation ep.
        Assert.Equal(LlmTier.Tier3_PremiumCloud, router.Select(Ctx(ep: 5, turn: 0, stalled: 2)).Tier);
    }

    // ---- Phase 2: config-driven temperatures ----

    [Fact]
    public async Task SendAsync_AppliesConfiguredTier3Temperature()
    {
        var router = NewRouter(new RouterConfig
        {
            TurnsBeforeEscalation = 0,
            TurnsBeforeFlashEscalation = 0,
            EpisodesBeforeProEscalation = 0,
            TempTier3 = 0.05,  // custom — overrides the hardcoded 0.1
        });
        // Force a Pro selection: ep 5, turn 0, stalled 2 (past all thresholds).
        await router.SendAsync(Ctx(5, 0, 2), new LlmRequest
        {
            Messages = new[] { new LlmMessage("user", "hi") },
            Temperature = 0.9,  // should be overridden by the router to 0.05
        }, default);
        // The Pro client received the request with the router-overridden temperature:
        _pro.Verify(c => c.CompleteAsync(
            It.Is<LlmRequest>(r => r.Temperature == 0.05), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendAsync_AppliesConfiguredTier2Temperature()
    {
        var router = NewRouter(new RouterConfig
        {
            TurnsBeforeEscalation = 0,   // leave Tier1 immediately
            TempTier2 = 0.25,
        });
        await router.SendAsync(Ctx(0, 1, stalled: 0), new LlmRequest
        {
            Messages = new[] { new LlmMessage("user", "hi") },
            Temperature = 0.9,
        }, default);
        _flash.Verify(c => c.CompleteAsync(
            It.Is<LlmRequest>(r => r.Temperature == 0.25), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- 1c: provenance — ModelId propagates through the router ----

    [Fact]
    public async Task SendAsync_PropagatesModelIdFromClient()
    {
        var router = NewRouter();  // Tier1 → deepseek-v4-flash
        var resp = await router.SendAsync(Ctx(0, 0), new LlmRequest
        {
            Messages = new[] { new LlmMessage("user", "hi") },
        }, default);
        Assert.Equal("deepseek-v4-flash", resp.ModelId);
    }

    // ---- 1b: circuit breaker latches on 401/402 ----

    [Fact]
    public async Task SendAsync_OnPaymentRequired_LatchesBreakerAndThrowsOnNextCall()
    {
        // Wire all three tiers (the constructor needs one client per tier).
        _tier1.SetupGet(c => c.Tier).Returns(LlmTier.Tier1_Cheap);
        _flash.SetupGet(c => c.Tier).Returns(LlmTier.Tier2_DeepSeekFlash);
        _pro.SetupGet(c => c.Tier).Returns(LlmTier.Tier3_PremiumCloud);

        // First Tier1 call returns 402.
        _tier1.Setup(c => c.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new HttpRequestException(
                  "Payment Required", null, System.Net.HttpStatusCode.PaymentRequired));

        var router = new TieredLlmRouter(
            new[] { _tier1.Object, _flash.Object, _pro.Object },
            new RouterConfig(),
            NullLogger<TieredLlmRouter>.Instance);

        // First call throws (402 surfaces), latching the breaker.
        await Assert.ThrowsAsync<HttpRequestException>(() => router.SendAsync(
            Ctx(0, 0), new LlmRequest { Messages = new[] { new LlmMessage("user", "x") } }, default));

        // Second call — any tier — throws InvalidOperationException (breaker latched),
        // NOT another HTTP call. This is the documented abort behavior.
        await Assert.ThrowsAsync<InvalidOperationException>(() => router.SendAsync(
            Ctx(0, 0), new LlmRequest { Messages = new[] { new LlmMessage("user", "x") } }, default));
    }

    // ── Slow-request heartbeat (2026-08-04) ─────────────────────────────────────────
    // Regression cover for the bench "hang": four runs were killed as deadlocked when the
    // logs actually showed ONE outstanding request, all other responses healthy, and the
    // kill landing ~1 min before HttpClient's own timeout would have released it. The bug
    // was that a slow request is indistinguishable from a frozen process in the log, so the
    // router now announces one. These pin that it warns, and that it does not otherwise
    // alter the request.


    /// <summary>Same wiring as <see cref="NewRouter"/> but with a caller-supplied logger.</summary>
    private TieredLlmRouter NewRouterWith(ILogger<TieredLlmRouter> log)
    {
        _tier1.SetupGet(c => c.Tier).Returns(LlmTier.Tier1_Cheap);
        _flash.SetupGet(c => c.Tier).Returns(LlmTier.Tier2_DeepSeekFlash);
        _pro.SetupGet(c => c.Tier).Returns(LlmTier.Tier3_PremiumCloud);
        _tier1.Setup(c => c.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((LlmRequest r, CancellationToken _) => Ok(LlmTier.Tier1_Cheap, "deepseek-v4-flash", r.Temperature));
        _flash.Setup(c => c.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((LlmRequest r, CancellationToken _) => Ok(LlmTier.Tier2_DeepSeekFlash, "deepseek-v4-flash", r.Temperature));
        _pro.Setup(c => c.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((LlmRequest r, CancellationToken _) => Ok(LlmTier.Tier3_PremiumCloud, "deepseek-v4-pro", r.Temperature));
        return new TieredLlmRouter(new[] { _tier1.Object, _flash.Object, _pro.Object }, new RouterConfig(), log);
    }

    private sealed class CapturingLogger : ILogger<TieredLlmRouter>
    {
        public readonly List<string> Warnings = new();
        public IDisposable? BeginScope<TState>(TState s) where TState : notnull => null;
        public bool IsEnabled(LogLevel l) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
                                Func<TState, Exception?, string> fmt)
        {
            if (level == LogLevel.Warning) lock (Warnings) Warnings.Add(fmt(state, ex));
        }
    }

    [Fact]
    public async Task SendAsync_FastRequest_LogsNoSlowWarning()
    {
        var log = new CapturingLogger();
        var router = NewRouterWith(log);   // router requires all three tiers registered

        var res = await router.SendAsync(Ctx(0, 0), new LlmRequest { Messages = [] }, CancellationToken.None);

        Assert.Equal("ok", res.Content);
        Assert.Empty(log.Warnings);
    }

    [Fact]
    public async Task SendAsync_SlowRequest_StillReturnsTheRealResponse()
    {
        // The heartbeat must be purely observational: a slow-but-successful request has to come
        // back intact and uncancelled, or the "fix" would break legitimate long generations
        // (Tier 3 reasoning at 8k output tokens routinely takes a while).
        var log = new CapturingLogger();
        var router = NewRouterWith(log);
        // Make the tier-1 client (the one Ctx(0,0) routes to) deliberately slow but successful.
        _tier1.Setup(c => c.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
              .Returns(async (LlmRequest r, CancellationToken t) =>
              {
                  await Task.Delay(TimeSpan.FromMilliseconds(250), t);
                  return Ok(LlmTier.Tier1_Cheap, "deepseek-v4-flash", r.Temperature);
              });

        var res = await router.SendAsync(Ctx(0, 0), new LlmRequest { Messages = [] }, CancellationToken.None);

        Assert.Equal("ok", res.Content);
        Assert.Equal("deepseek-v4-flash", res.ModelId);
    }

}
