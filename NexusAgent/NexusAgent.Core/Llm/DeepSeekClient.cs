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
/// DeepSeek client. Uses the OpenAI-compatible chat completions endpoint.
/// Same client class handles both tiers — the model id is set per instance.
///
/// Valid model names (May 2026):
///   deepseek-chat     — DeepSeek-V3, fast general coding  (Tier 1 + Tier 2)
///   deepseek-reasoner — DeepSeek-R1, complex reasoning    (Tier 3)
///
/// Pricing (per million tokens, USD, approximate):
///   deepseek-chat:     $0.27/M input (cache miss), $0.07/M cached, $1.10/M output
///   deepseek-reasoner: $0.55/M input (cache miss), $0.14/M cached, $2.19/M output
///
/// Cache hit pricing is ~4x cheaper. Our prompts are designed for prefix-cache
/// hits — the system prompt + sketch prefix is stable across turns of an episode.
/// </summary>
public sealed class DeepSeekClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly NexusConfig _config;
    private readonly ILogger<DeepSeekClient> _log;
    private readonly string _modelId;
    private readonly decimal _inputPricePerMillion;
    private readonly decimal _cachedInputPricePerMillion;
    private readonly decimal _outputPricePerMillion;

    public LlmTier Tier { get; }

    private DeepSeekClient(
        HttpClient http,
        NexusConfig config,
        ILogger<DeepSeekClient> log,
        LlmTier tier,
        string modelId,
        decimal inputPrice,
        decimal cachedInputPrice,
        decimal outputPrice)
    {
        _http = http;
        _config = config;
        _log = log;
        Tier = tier;
        _modelId = modelId;
        _inputPricePerMillion = inputPrice;
        _cachedInputPricePerMillion = cachedInputPrice;
        _outputPricePerMillion = outputPrice;
    }

    /// <summary>Tier 1 instance — replaces Qwen local. Same model as Flash but used for
    /// early turns (higher temperature, exploratory). No local GPU required.</summary>
    public static DeepSeekClient Tier1(
        HttpClient http, IOptions<NexusConfig> config, ILogger<DeepSeekClient> log)
        => new(http, config.Value, log,
            LlmTier.Tier1_Cheap,
            "deepseek-chat",
            inputPrice: 0.27m,
            cachedInputPrice: 0.07m,
            outputPrice: 1.10m);

    public static DeepSeekClient Flash(
        HttpClient http, IOptions<NexusConfig> config, ILogger<DeepSeekClient> log)
        => new(http, config.Value, log,
            LlmTier.Tier2_DeepSeekFlash,
            "deepseek-chat",
            inputPrice: 0.27m,
            cachedInputPrice: 0.07m,
            outputPrice: 1.10m);

    public static DeepSeekClient Pro(
        HttpClient http, IOptions<NexusConfig> config, ILogger<DeepSeekClient> log)
        => new(http, config.Value, log,
            LlmTier.Tier3_PremiumCloud,
            "deepseek-reasoner",
            inputPrice: 0.55m,
            cachedInputPrice: 0.14m,
            outputPrice: 2.19m);

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // deepseek-reasoner's max_tokens budget covers BOTH thinking tokens AND the answer.
        // 2048 is exhausted entirely by the reasoning chain — the model never reaches the
        // answer, so content comes back null. Use at least 8192 for the reasoner model.
        var effectiveMaxTokens = _modelId == "deepseek-reasoner"
            ? Math.Max(request.MaxOutputTokens, 8192)
            : request.MaxOutputTokens;

        var apiRequest = new ChatCompletionRequest
        {
            Model = _modelId,
            Messages = request.Messages
                .Select(m => new ChatMessage(m.Role, m.Content))
                .ToArray(),
            Temperature = request.Temperature,
            MaxTokens = effectiveMaxTokens,
            Stream = false,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{_config.DeepSeekBaseUrl.TrimEnd('/')}/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.DeepSeekApiKey);
        req.Content = JsonContent.Create(apiRequest, DeepSeekJsonContext.Default.ChatCompletionRequest);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _log.LogError("DeepSeek API error {Status}: {Body}", resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }

        var payload = await resp.Content.ReadFromJsonAsync(
            DeepSeekJsonContext.Default.ChatCompletionResponse, ct)
            ?? throw new InvalidOperationException("DeepSeek returned empty response");

        sw.Stop();

        var choice = payload.Choices.FirstOrDefault();
        var content = choice?.Message.Content;

        // DeepSeek-R1 (reasoner) returns "content": null for hard problems where the
        // answer is only in the internal reasoning chain (reasoning_content).
        // Fall back to reasoning_content so ExtractLeanFromResponse can still find code.
        if (string.IsNullOrEmpty(content) && !string.IsNullOrEmpty(choice?.Message.ReasoningContent))
        {
            content = choice!.Message.ReasoningContent;
            _log.LogDebug(
                "DeepSeek {Model}: content was null/empty — falling back to reasoning_content ({Len} chars)",
                _modelId, content.Length);
        }

        content ??= "";

        var usage = payload.Usage;
        var cachedTokens = usage?.PromptCacheHitTokens ?? 0;
        var newInputTokens = (usage?.PromptTokens ?? 0) - cachedTokens;
        var outputTokens = usage?.CompletionTokens ?? 0;

        var cost = (cachedTokens * _cachedInputPricePerMillion / 1_000_000m)
                 + (newInputTokens * _inputPricePerMillion / 1_000_000m)
                 + (outputTokens * _outputPricePerMillion / 1_000_000m);

        _log.LogDebug(
            "DeepSeek {Model}: {NewIn} new + {Cached} cached input, {Out} output → ${Cost:F4}",
            _modelId, newInputTokens, cachedTokens, outputTokens, cost);

        return new LlmResponse
        {
            Content = content,
            Tier = Tier,
            InputTokens = newInputTokens,
            OutputTokens = outputTokens,
            CachedInputTokens = cachedTokens,
            EstimatedCostUsd = cost,
            Latency = sw.Elapsed,
            FinishReason = payload.Choices.FirstOrDefault()?.FinishReason,
        };
    }
}

internal sealed record ChatCompletionRequest
{
    [JsonPropertyName("model")]       public required string Model { get; init; }
    [JsonPropertyName("messages")]    public required ChatMessage[] Messages { get; init; }
    [JsonPropertyName("temperature")] public double Temperature { get; init; }
    [JsonPropertyName("max_tokens")]  public int MaxTokens { get; init; }
    [JsonPropertyName("stream")]      public bool Stream { get; init; }
}

internal sealed record ChatMessage(
    [property: JsonPropertyName("role")]    string Role,
    [property: JsonPropertyName("content")] string Content);

internal sealed record ChatCompletionResponse
{
    [JsonPropertyName("id")]      public string Id { get; init; } = "";
    [JsonPropertyName("model")]   public string Model { get; init; } = "";
    [JsonPropertyName("choices")] public ChatChoice[] Choices { get; init; } = [];
    [JsonPropertyName("usage")]   public Usage? Usage { get; init; }
}

internal sealed record ChatChoice
{
    [JsonPropertyName("index")]         public int Index { get; init; }
    [JsonPropertyName("message")]       public AssistantMessage Message { get; init; } = new();
    [JsonPropertyName("finish_reason")] public string? FinishReason { get; init; }
}

/// <summary>
/// Response-side assistant message. Separating from the request <see cref="ChatMessage"/>
/// so that <c>Content</c> can be nullable (DeepSeek-R1 returns <c>"content": null</c>
/// when reasoning is in <c>reasoning_content</c> only) and <c>ReasoningContent</c>
/// can be captured as a fallback source of Lean code.
/// </summary>
internal sealed class AssistantMessage
{
    [JsonPropertyName("role")]              public string Role { get; init; } = "assistant";
    [JsonPropertyName("content")]           public string? Content { get; init; }
    [JsonPropertyName("reasoning_content")] public string? ReasoningContent { get; init; }
}

internal sealed record Usage
{
    [JsonPropertyName("prompt_tokens")]            public int PromptTokens { get; init; }
    [JsonPropertyName("completion_tokens")]        public int CompletionTokens { get; init; }
    [JsonPropertyName("total_tokens")]             public int TotalTokens { get; init; }
    [JsonPropertyName("prompt_cache_hit_tokens")]  public int PromptCacheHitTokens { get; init; }
    [JsonPropertyName("prompt_cache_miss_tokens")] public int PromptCacheMissTokens { get; init; }
}

[JsonSerializable(typeof(ChatCompletionRequest))]
[JsonSerializable(typeof(ChatCompletionResponse))]
[JsonSerializable(typeof(AssistantMessage))]
internal sealed partial class DeepSeekJsonContext : JsonSerializerContext;
