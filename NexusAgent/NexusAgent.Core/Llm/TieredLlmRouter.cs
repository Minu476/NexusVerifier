using Microsoft.Extensions.Logging;
using NexusAgent.Core.Models;

namespace NexusAgent.Core.Llm;

/// <summary>
/// Routes LLM requests across the four tiers based on episode state, problem
/// difficulty, and an explicit budget ceiling.
///
/// Routing policy:
///   1. Fossil hits never reach the router (handled upstream in NexusProverSubagent).
///   2. First N turns of any episode → Tier 1 (Qwen local, free).
///   3. After Tier 1 stalls (≥ K turns with no sorry reduction) → Tier 2 (Flash).
///   4. After Tier 2 stalls or episode passes mid-budget → Tier 3 (Pro).
///   5. If running budget would exceed ceiling → fall back to Tier 1 only.
/// </summary>
public sealed class TieredLlmRouter
{
    private readonly ILlmClient _qwen;
    private readonly ILlmClient _flash;
    private readonly ILlmClient _pro;
    private readonly ILogger<TieredLlmRouter> _log;
    private readonly RouterConfig _cfg;
    private decimal _spentUsd;

    public decimal SpentUsd => _spentUsd;
    public decimal RemainingBudgetUsd => _cfg.BudgetCapUsd - _spentUsd;

    public TieredLlmRouter(
        IEnumerable<ILlmClient> clients,
        RouterConfig config,
        ILogger<TieredLlmRouter> log)
    {
        var clientArray = clients.ToArray();
        _qwen  = clientArray.First(c => c.Tier == LlmTier.Tier1_QwenLocal);
        _flash = clientArray.First(c => c.Tier == LlmTier.Tier2_DeepSeekFlash);
        _pro   = clientArray.First(c => c.Tier == LlmTier.Tier3_PremiumCloud);
        _cfg = config;
        _log = log;
    }

    public ILlmClient Select(RouterContext ctx)
    {
        // Hard budget check first — never overspend.
        if (_spentUsd >= _cfg.BudgetCapUsd)
        {
            _log.LogWarning("Budget cap ${Cap:F2} reached; forcing Tier 1 (Qwen local)", _cfg.BudgetCapUsd);
            return _qwen;
        }

        // Escalation ladder
        if (ctx.TurnIndex < _cfg.TurnsBeforeEscalation)
            return _qwen;

        if (ctx.TurnsSinceLastProgress < _cfg.TurnsBeforeFlashEscalation
            && ctx.EpisodeIndex < _cfg.EpisodesBeforeProEscalation)
            return _flash;

        // Hard problems / late episodes → escalate to V4-Pro
        return _pro;
    }

    public async Task<LlmResponse> SendAsync(
        RouterContext ctx, LlmRequest request, CancellationToken ct)
    {
        var client = Select(ctx);
        var response = await client.CompleteAsync(request, ct);
        _spentUsd += response.EstimatedCostUsd;
        return response;
    }
}

public sealed record RouterContext
{
    public required int EpisodeIndex { get; init; }
    public required int TurnIndex { get; init; }
    public required int TurnsSinceLastProgress { get; init; }
    public required int CurrentSorryCount { get; init; }
}

public sealed record RouterConfig
{
    public decimal BudgetCapUsd { get; init; } = 200m;
    public int TurnsBeforeEscalation { get; init; } = 3;
    public int TurnsBeforeFlashEscalation { get; init; } = 4;
    public int EpisodesBeforeProEscalation { get; init; } = 20;
}
