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
        // Reasoning models (Tier 3) benefit from low temperature — their chain-of-thought
        // already provides the exploration. Flash (Tier 2) works best at 0.3 — enough
        // diversity to escape local minima without the noise of 0.4. Qwen local (Tier 1)
        // keeps the default 0.4 for exploratory breadth.
        var effectiveRequest = client.Tier switch
        {
            LlmTier.Tier3_PremiumCloud  => request with { Temperature = 0.1 },
            LlmTier.Tier2_DeepSeekFlash => request with { Temperature = 0.3 },
            _                           => request,  // Tier1: keep 0.4 for exploratory breadth
        };
        try
        {
            var response = await client.CompleteAsync(effectiveRequest, ct);
            lock (_spentLock) { _spentUsd += response.EstimatedCostUsd; }
            return response;
        }
        catch (HttpRequestException ex) when
            (ex.StatusCode == System.Net.HttpStatusCode.PaymentRequired ||
             ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            // Latch the circuit breaker — no fallback model available.
            _deepSeekUnavailable = true;
            _log.LogError(
                "DeepSeek API unavailable ({Status}); circuit breaker latched — all further calls will throw",
                ex.StatusCode);
            throw;
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
}
