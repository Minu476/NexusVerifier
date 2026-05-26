using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexusAgent.Core.Configuration;
using NexusAgent.Core.Models;

namespace NexusAgent.Core.Llm;

/// <summary>
/// Qwen3-max cloud client via Alibaba DashScope OpenAI-compatible API.
/// Used as Tier 3 (premium) when local Qwen + DeepSeek Flash cannot close a goal.
///
/// Endpoint: https://dashscope-intl.aliyuncs.com/compatible-mode/v1
/// Auth:     Authorization: Bearer $DASHSCOPE_API_KEY
/// Model:    qwen-max  (Qwen3-235B-A22B, full MoE inference — 22B active params)
///
/// DashScope pricing (international, as of May 2026):
///   Input:        $3.23 / M tokens
///   Input cached: $0.32 / M tokens  (~10× cheaper on prompt-cache hits)
///   Output:       $9.58 / M tokens
///
/// DashScope usage field differs from DeepSeek — cache hits are returned in
///   usage.prompt_tokens_details.cached_tokens  (OpenAI spec format)
/// rather than usage.prompt_cache_hit_tokens (DeepSeek-specific).
/// </summary>
public sealed class QwenCloudClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly NexusConfig _config;
    private readonly ILogger<QwenCloudClient> _log;

    // Pricing constants (per million tokens)
    private const decimal InputPricePerMillion       = 3.23m;
    private const decimal CachedInputPricePerMillion = 0.32m;
    private const decimal OutputPricePerMillion      = 9.58m;

    public LlmTier Tier => LlmTier.Tier3_PremiumCloud;

    public QwenCloudClient(
        HttpClient http,
        IOptions<NexusConfig> config,
        ILogger<QwenCloudClient> log)
    {
        _http   = http;
        _config = config.Value;
        _log    = log;
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        var apiRequest = new QwenChatRequest
        {
            Model    = _config.QwenCloudModelTag,
            Messages = request.Messages
                .Select(m => new QwenChatMessage(m.Role, m.Content))
                .ToArray(),
            Temperature     = request.Temperature,
            MaxTokens       = request.MaxOutputTokens,
            Stream          = false,
            // Enable DashScope prefix-caching (reduces cost 10× for repeated prefixes)
            EnableSearch    = false,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{_config.DashScopeBaseUrl.TrimEnd('/')}/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.DashScopeApiKey);
        req.Content = JsonContent.Create(apiRequest, QwenCloudJsonContext.Default.QwenChatRequest);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _log.LogError("DashScope API error {Status}: {Body}", resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }

        var payload = await resp.Content.ReadFromJsonAsync(
            QwenCloudJsonContext.Default.QwenChatResponse, ct)
            ?? throw new InvalidOperationException("DashScope returned empty response");

        sw.Stop();

        var content      = payload.Choices.FirstOrDefault()?.Message.Content ?? "";
        var usage        = payload.Usage;
        var cachedTokens = usage?.PromptTokensDetails?.CachedTokens ?? 0;
        var promptTotal  = usage?.PromptTokens ?? 0;
        var newInput     = promptTotal - cachedTokens;
        var outputTokens = usage?.CompletionTokens ?? 0;

        var cost = (cachedTokens * CachedInputPricePerMillion / 1_000_000m)
                 + (newInput     * InputPricePerMillion       / 1_000_000m)
                 + (outputTokens * OutputPricePerMillion      / 1_000_000m);

        _log.LogDebug(
            "QwenCloud {Model}: {NewIn} new + {Cached} cached input, {Out} output → ${Cost:F4}",
            _config.QwenCloudModelTag, newInput, cachedTokens, outputTokens, cost);

        return new LlmResponse
        {
            Content           = content,
            Tier              = Tier,
            InputTokens       = newInput,
            OutputTokens      = outputTokens,
            CachedInputTokens = cachedTokens,
            EstimatedCostUsd  = cost,
            Latency           = sw.Elapsed,
            FinishReason      = payload.Choices.FirstOrDefault()?.FinishReason,
        };
    }
}

// ---------------------------------------------------------------------------
// DashScope OpenAI-compatible JSON types
// ---------------------------------------------------------------------------

internal sealed record QwenChatRequest
{
    [JsonPropertyName("model")]          public required string Model { get; init; }
    [JsonPropertyName("messages")]       public required QwenChatMessage[] Messages { get; init; }
    [JsonPropertyName("temperature")]    public double Temperature { get; init; }
    [JsonPropertyName("max_tokens")]     public int MaxTokens { get; init; }
    [JsonPropertyName("stream")]         public bool Stream { get; init; }
    // DashScope-specific: disable web search (we want deterministic proof output)
    [JsonPropertyName("enable_search")]  public bool EnableSearch { get; init; }
}

internal sealed record QwenChatMessage(
    [property: JsonPropertyName("role")]    string Role,
    [property: JsonPropertyName("content")] string Content);

internal sealed record QwenChatResponse
{
    [JsonPropertyName("id")]      public string Id { get; init; } = "";
    [JsonPropertyName("model")]   public string Model { get; init; } = "";
    [JsonPropertyName("choices")] public QwenChatChoice[] Choices { get; init; } = [];
    [JsonPropertyName("usage")]   public QwenUsage? Usage { get; init; }
}

internal sealed record QwenChatChoice
{
    [JsonPropertyName("index")]         public int Index { get; init; }
    [JsonPropertyName("message")]       public QwenChatMessage Message { get; init; } = new("assistant", "");
    [JsonPropertyName("finish_reason")] public string? FinishReason { get; init; }
}

/// <summary>
/// DashScope usage follows the OpenAI spec: cache hits are nested under
/// <c>prompt_tokens_details.cached_tokens</c> (not the DeepSeek-specific
/// <c>prompt_cache_hit_tokens</c> top-level field).
/// </summary>
internal sealed record QwenUsage
{
    [JsonPropertyName("prompt_tokens")]         public int PromptTokens { get; init; }
    [JsonPropertyName("completion_tokens")]     public int CompletionTokens { get; init; }
    [JsonPropertyName("total_tokens")]          public int TotalTokens { get; init; }
    [JsonPropertyName("prompt_tokens_details")] public QwenPromptTokensDetails? PromptTokensDetails { get; init; }
}

internal sealed record QwenPromptTokensDetails
{
    [JsonPropertyName("cached_tokens")] public int CachedTokens { get; init; }
}

[JsonSerializable(typeof(QwenChatRequest))]
[JsonSerializable(typeof(QwenChatResponse))]
internal sealed partial class QwenCloudJsonContext : JsonSerializerContext;
