using System.Net.Http;
using Microsoft.Extensions.Logging;
using NexusAgent.Core.Models;

namespace NexusAgent.Core.Llm;

/// <summary>
/// Routes LLM requests across the four tiers based on episode state, problem
/// difficulty, and an explicit budget ceiling.
///
/// Routing policy:
///   1. Fossil hits never reach the router (handled upstream in NexusProverSubagent).
///   2. First N turns of any episode → Tier 1 (DeepSeek-chat, exploratory temp=0.4).
///   3. After Tier 1 stalls (≥ K turns with no sorry reduction) → Tier 2 (DeepSeek-chat, temp=0.3).
///   4. After Tier 2 stalls or episode passes mid-budget → Tier 3 (DeepSeek-reasoner, temp=0.1).
///   5. If budget ceiling reached → log warning; in-flight calls complete normally.
/// </summary>
public sealed class TieredLlmRouter
{
    private readonly ILlmClient _tier1;  // DeepSeek-chat, exploratory turns
    private readonly ILlmClient _flash;  // DeepSeek-chat, focused turns
    private readonly ILlmClient _pro;    // DeepSeek-reasoner, hard problems
    private readonly ILogger<TieredLlmRouter> _log;
    private readonly RouterConfig _cfg;
    private decimal _spentUsd;
    private readonly object _spentLock = new();
    // Circuit breaker: once DeepSeek returns 402/401, abort remaining calls.
    private volatile bool _deepSeekUnavailable;

    public decimal SpentUsd { get { lock (_spentLock) return _spentUsd; } }
    public decimal RemainingBudgetUsd { get { lock (_spentLock) return _cfg.BudgetCapUsd - _spentUsd; } }

    public TieredLlmRouter(
        IEnumerable<ILlmClient> clients,
        RouterConfig config,
        ILogger<TieredLlmRouter> log)
    {
        var clientArray = clients.ToArray();
        _tier1 = clientArray.First(c => c.Tier == LlmTier.Tier1_Cheap);
        _flash = clientArray.First(c => c.Tier == LlmTier.Tier2_DeepSeekFlash);
        _pro   = clientArray.First(c => c.Tier == LlmTier.Tier3_PremiumCloud);
        _cfg = config;
        _log = log;
    }

    public ILlmClient Select(RouterContext ctx)
    {
        // Hard budget check — log but allow in-flight calls to complete.
        if (RemainingBudgetUsd <= 0)
            _log.LogWarning("Budget cap ${Cap:F2} reached", _cfg.BudgetCapUsd);

        // Circuit breaker: if DeepSeek returned 402/401, abort further calls.
        if (_deepSeekUnavailable)
            throw new InvalidOperationException("DeepSeek API unavailable (402/401); aborting.");

        // Escalation ladder
        ILlmClient selected;
        if (ctx.TurnIndex < _cfg.TurnsBeforeEscalation)
            selected = _tier1;
        else if (ctx.TurnsSinceLastProgress < _cfg.TurnsBeforeFlashEscalation
            && ctx.EpisodeIndex < _cfg.EpisodesBeforeProEscalation)
            selected = _flash;
        else
            // Hard problems / late episodes → escalate to V4-Pro
            selected = _pro;

        // Apply tier ceiling: demote if a structural violation locked us out of Tier 3.
        if (ctx.TierCeiling.HasValue && (int)selected.Tier > (int)ctx.TierCeiling.Value)
        {
            var capped = ctx.TierCeiling.Value switch
            {
                LlmTier.Tier1_Cheap         => _tier1,
                LlmTier.Tier2_DeepSeekFlash => _flash,
                _                           => selected,
            };
            _log.LogInformation(
                "Tier ceiling {Ceiling} applied — demoting from {Sel} to {Capped}",
                ctx.TierCeiling.Value, selected.Tier, capped.Tier);
            return capped;
        }

        return selected;
    }

    public async Task<LlmResponse> SendAsync(
        RouterContext ctx, LlmRequest request, CancellationToken ct)
    {
        var client = Select(ctx);
        // Per-tier sampling temperatures. Defaults (Tier1=0.4 exploratory breadth,
        // Tier2=0.3 to escape local minima, Tier3=0.1 because reasoning models need low
        // temp) are overridable via NEXUS_TEMP_TIER1/2/3 so an aggressive run can tune
        // sampling without recompiling. See NexusConfig.ApplyEnvironmentOverrides.
        var effectiveRequest = client.Tier switch
        {
            LlmTier.Tier3_PremiumCloud  => request with { Temperature = _cfg.TempTier3 },
            LlmTier.Tier2_DeepSeekFlash => request with { Temperature = _cfg.TempTier2 },
            LlmTier.Tier1_Cheap         => request with { Temperature = _cfg.TempTier1 },
            _                           => request,
        };
        try
        {
            // Heartbeat while a request is outstanding.
            //
            // Why this exists (2026-08-04): four bench runs were killed as "hung under parallel
            // load", and hours went into a suspected Neo4j async-session deadlock. The logs say
            // otherwise — at the stall there was exactly ONE request in flight (231 sent, 230
            // received), every other response was 200 with p99 latency under 500ms, and the
            // process was killed roughly a minute BEFORE HttpClient's 5-minute timeout would
            // have fired and let the run continue. A single stalled request is
            // parallelism-independent, which is exactly why dropping to parallelism 3 "didn't
            // help" and reinforced the wrong diagnosis.
            //
            // The bug was never a deadlock; it was that a slow request is indistinguishable from
            // a frozen process in the log. This makes the difference visible so nobody kills a
            // healthy run again.
            var response = await WithSlowRequestHeartbeat(
                client.CompleteAsync(effectiveRequest, ct), client.Tier, ct);
            lock (_spentLock) { _spentUsd += response.EstimatedCostUsd; }
            return response;
        }
        catch (HttpRequestException ex) when
            (ex.StatusCode == System.Net.HttpStatusCode.PaymentRequired ||
             ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            // Latch the circuit breaker — no fallback model available. This is a
            // process-level state (bad key or exhausted billing) that retrying
            // won't fix: once latched, every remaining problem's LLM call throws
            // InvalidOperationException, so the in-flight problems finish failing
            // and no new work can make progress. Distinct from transient 429/5xx,
            // which are retried inside DeepSeekClient and never reach here.
            _deepSeekUnavailable = true;
            _log.LogError(
                "DeepSeek API unavailable ({Status}); circuit breaker latched — this is a "
                + "credentials/billing state (not transient), so the remaining run cannot make "
                + "LLM progress. Spent so far: ${Spent:F2} of ${Cap:F2}.",
                ex.StatusCode, SpentUsd, _cfg.BudgetCapUsd);
            throw;
        }
    }

    /// <summary>First warn after this long; then every <see cref="SlowRequestInterval"/>.</summary>
    private static readonly TimeSpan SlowRequestThreshold = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan SlowRequestInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Awaits <paramref name="inFlight"/>, logging a warning if it stays outstanding long enough
    /// to look like a hang. Purely observational: it never cancels or alters the request, so a
    /// legitimately slow generation (Tier 3 reasoning at 8k output tokens) completes exactly as
    /// before — it just stops being silent. The underlying bound remains HttpClient's own
    /// timeout, configured per client in Program.cs.
    /// </summary>
    private async Task<LlmResponse> WithSlowRequestHeartbeat(
        Task<LlmResponse> inFlight, LlmTier tier, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var delay = SlowRequestThreshold;
        while (true)
        {
            // Losing the race means the request is still outstanding — say so and keep waiting.
            var tick = Task.Delay(delay, ct);
            if (await Task.WhenAny(inFlight, tick) == inFlight)
                return await inFlight;

            _log.LogWarning(
                "LLM request to {Tier} still outstanding after {Elapsed:F0}s — the run is WAITING, "
                + "not deadlocked; it is bounded by the HttpClient timeout. Do not kill the process "
                + "on the strength of a quiet log.",
                tier, sw.Elapsed.TotalSeconds);
            delay = SlowRequestInterval;
        }
    }
}

public sealed record RouterContext
{
    public required int EpisodeIndex { get; init; }
    public required int TurnIndex { get; init; }
    public required int TurnsSinceLastProgress { get; init; }
    public required int CurrentSorryCount { get; init; }
    /// <summary>
    /// When set, the router will not escalate above this tier for the current episode.
    /// Used to demote away from deepseek-reasoner after a structural violation.
    /// </summary>
    public LlmTier? TierCeiling { get; init; }
}

public sealed record RouterConfig
{
    public decimal BudgetCapUsd { get; init; } = 200m;
    public int TurnsBeforeEscalation { get; init; } = 3;
    public int TurnsBeforeFlashEscalation { get; init; } = 6;  // raised from 4 to reduce premature Pro escalation
    public int EpisodesBeforeProEscalation { get; init; } = 20;
    /// <summary>Sampling temperature per tier. Defaults match the prior hardcoded values;
    /// overridable via NEXUS_TEMP_TIER1/2/3 for tuning an aggressive run without recompiling.</summary>
    public double TempTier1 { get; init; } = 0.4;
    public double TempTier2 { get; init; } = 0.3;
    public double TempTier3 { get; init; } = 0.1;
}
