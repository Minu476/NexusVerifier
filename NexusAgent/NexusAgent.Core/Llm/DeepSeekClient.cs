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
/// DeepSeek V4 client. Uses the OpenAI-compatible chat completions endpoint.
/// Same client class handles both V4-Flash and V4-Pro — the model id is set
/// per instance via the tier.
///
/// Pricing as of May 2026 (verified May 22, 2026):
///   V4-Flash: $0.14/M input (cache miss), $0.0028/M cached, $0.28/M output
///   V4-Pro:   $0.435/M input (cache miss), $0.003625/M cached, $0.87/M output
///
/// Cache hit pricing is ~50x cheaper. Our prompts are designed for prefix-cache
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

    public static DeepSeekClient Flash(
        HttpClient http, IOptions<NexusConfig> config, ILogger<DeepSeekClient> log)
        => new(http, config.Value, log,
            LlmTier.Tier2_DeepSeekFlash,
            "deepseek-v4-flash",
            inputPrice: 0.14m,
            cachedInputPrice: 0.0028m,
            outputPrice: 0.28m);

    public static DeepSeekClient Pro(
        HttpClient http, IOptions<NexusConfig> config, ILogger<DeepSeekClient> log)
        => new(http, config.Value, log,
            LlmTier.Tier3_PremiumCloud,
            "deepseek-v4-pro",
            inputPrice: 0.435m,
            cachedInputPrice: 0.003625m,
            outputPrice: 0.87m);

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        var apiRequest = new ChatCompletionRequest
        {
            Model = _modelId,
            Messages = request.Messages
                .Select(m => new ChatMessage(m.Role, m.Content))
                .ToArray(),
            Temperature = request.Temperature,
            MaxTokens = request.MaxOutputTokens,
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

        var content = payload.Choices.FirstOrDefault()?.Message.Content ?? "";
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
    [JsonPropertyName("message")]       public ChatMessage Message { get; init; } = new("assistant", "");
    [JsonPropertyName("finish_reason")] public string? FinishReason { get; init; }
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
internal sealed partial class DeepSeekJsonContext : JsonSerializerContext;
