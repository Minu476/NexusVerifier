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
/// DeepSeek client. Handles both v4 tiers via a HYBRID of two OpenAI-compatible surfaces:
///
///   • Tier 1 + Tier 2 (deepseek-v4-flash)  → Responses API  (POST {base}/responses)
///   • Tier 3      (deepseek-v4-pro)        → Chat Completions (POST {base}/chat/completions)
///
/// Why the split: the Responses API exposes `reasoning.effort`, which lets exploratory
/// Tier 1/2 calls run with little or no reasoning-token spend (verified: effort=none →
/// 0 reasoning tokens vs effort=high → 22, on an identical prompt). The prover makes
/// thousands of calls per run, so suppressing the reasoning tax on early tiers is a real
/// cost/speed win for aggressive use. deepseek-v4-pro is NOT yet available on the
/// Responses API ("available early August 2026" per the API error), so Tier 3 stays on
/// Chat Completions where it works today. Both paths share the retry + cost logic.
///
/// Valid model names (confirmed 2026-07-31):
///   deepseek-v4-flash — fast general coding, supports reasoning.effort (Tier 1 + Tier 2)
///   deepseek-v4-pro   — complex reasoning, Chat Completions only (Tier 3)
///
/// Pricing (per million tokens, USD — confirmed against DeepSeek's pricing page,
/// https://api-docs.deepseek.com/quick_start/pricing/, on 2026-07-31):
///   deepseek-v4-flash: $0.14   /M input (cache miss), $0.0028   /M cached, $0.28 /M output
///   deepseek-v4-pro:   $0.435  /M input (cache miss), $0.003625 /M cached, $0.87 /M output
///
/// DeepSeek's docs note prices are 2x the listed rates during peak hours (start date TBD);
/// we charge the off-peak rate and accept that real spend under load may run higher than
/// EstimatedCostUsd. Cache hit pricing is ~50-120x cheaper; our prompts are designed for
/// prefix-cache hits — the system prompt + sketch prefix is stable across turns.
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
    /// <summary>Reasoning effort for the Responses API (Tier 1/2 only). Tier 3 ignores it.</summary>
    private readonly string _reasoningEffort;

    public LlmTier Tier { get; }

    /// <summary>Base backoff (seconds) before retries. Indexed by attempt number (1-based);
    /// index 0 is unused (the first attempt never waits). Jitter is added at retry time.</summary>
    private static readonly double[] RetryBackoffSec = { 0, 0, 2, 4, 8 };

    private DeepSeekClient(
        HttpClient http,
        NexusConfig config,
        ILogger<DeepSeekClient> log,
        LlmTier tier,
        string modelId,
        decimal inputPrice,
        decimal cachedInputPrice,
        decimal outputPrice,
        string reasoningEffort)
    {
        _http = http;
        _config = config;
        _log = log;
        Tier = tier;
        _modelId = modelId;
        _inputPricePerMillion = inputPrice;
        _cachedInputPricePerMillion = cachedInputPrice;
        _outputPricePerMillion = outputPrice;
        _reasoningEffort = reasoningEffort;
    }

    /// <summary>Tier 1 instance — replaces Qwen local. Same model as Flash but used for
    /// early turns (higher temperature, exploratory, NO reasoning to stay cheap/fast).</summary>
    public static DeepSeekClient Tier1(
        HttpClient http, IOptions<NexusConfig> config, ILogger<DeepSeekClient> log)
        => new(http, config.Value, log,
            LlmTier.Tier1_Cheap,
            "deepseek-v4-flash",
            inputPrice: 0.14m,
            cachedInputPrice: 0.0028m,
            outputPrice: 0.28m,
            reasoningEffort: ReasoningEffort("TIER1", defaultEffort: "none"));

    public static DeepSeekClient Flash(
        HttpClient http, IOptions<NexusConfig> config, ILogger<DeepSeekClient> log)
        => new(http, config.Value, log,
            LlmTier.Tier2_DeepSeekFlash,
            "deepseek-v4-flash",
            inputPrice: 0.14m,
            cachedInputPrice: 0.0028m,
            outputPrice: 0.28m,
            reasoningEffort: ReasoningEffort("TIER2", defaultEffort: "low"));

    public static DeepSeekClient Pro(
        HttpClient http, IOptions<NexusConfig> config, ILogger<DeepSeekClient> log)
        => new(http, config.Value, log,
            LlmTier.Tier3_PremiumCloud,
            "deepseek-v4-pro",
            inputPrice: 0.435m,
            cachedInputPrice: 0.003625m,
            outputPrice: 0.87m,
            reasoningEffort: "high");  // unused — Pro uses Chat Completions

    /// <summary>Resolves a per-tier reasoning effort (none|low|medium|high) from the
    /// NEXUS_REASONING_EFFORT_TIER1 / _TIER2 env var, falling back to the tier default.
    /// Tier 3 (pro) is always "high" (unused — it routes through Chat Completions).</summary>
    private static string ReasoningEffort(string tierKey, string defaultEffort)
    {
        var env = Environment.GetEnvironmentVariable($"NEXUS_REASONING_EFFORT_{tierKey}")?.Trim().ToLowerInvariant();
        return env is "none" or "low" or "medium" or "high" ? env : defaultEffort;
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Route by model: flash → Responses API, pro → Chat Completions.
        var (endpoint, payload) = IsResponsesModel(_modelId)
            ? BuildResponsesCall(request)
            : BuildChatCall(request);

        // Transient retry wraps BOTH endpoints. Auth/billing (401/402) is NOT retried here —
        // the router latches those in its circuit breaker. See SendWithRetryAsync docs.
        using var resp = await SendWithRetryAsync(endpoint, payload, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _log.LogError("DeepSeek API error {Status} on {Endpoint}: {Body}",
                resp.StatusCode, endpoint, body);
            resp.EnsureSuccessStatusCode();
        }

        // Parse per endpoint — each has a different JSON shape.
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        var (content, usage) = IsResponsesModel(_modelId)
            ? await ParseResponsesAsync(stream, ct)
            : await ParseChatAsync(stream, ct);

        sw.Stop();

        // Fallback: if the model returned no content, surface the reasoning text (if any)
        // so ExtractLeanFromResponse can still find code the model only "thought".
        if (string.IsNullOrEmpty(content))
            content = usage.ReasoningText ?? "";
        content ??= "";

        var cost = (usage.CachedTokens * _cachedInputPricePerMillion / 1_000_000m)
                 + (usage.NewInputTokens * _inputPricePerMillion / 1_000_000m)
                 + (usage.OutputTokens * _outputPricePerMillion / 1_000_000m);

        _log.LogDebug(
            "DeepSeek {Model} ({Ep}): {NewIn} new + {Cached} cached input, {Out} output ({Reason} reasoning) → ${Cost:F4}",
            _modelId, IsResponsesModel(_modelId) ? "responses" : "chat",
            usage.NewInputTokens, usage.CachedTokens, usage.OutputTokens, usage.ReasoningTokens, cost);

        return new LlmResponse
        {
            Content = content,
            Tier = Tier,
            ModelId = _modelId,
            InputTokens = usage.NewInputTokens,
            OutputTokens = usage.OutputTokens,
            CachedInputTokens = usage.CachedTokens,
            EstimatedCostUsd = cost,
            Latency = sw.Elapsed,
            FinishReason = usage.FinishReason,
        };
    }

    // ---- endpoint routing ----

    private static bool IsResponsesModel(string modelId) => modelId == "deepseek-v4-flash";

    // ---- Responses API request/response (Tier 1/2: flash) ----

    private (string endpoint, JsonContent payload) BuildResponsesCall(LlmRequest request)
    {
        // input accepts either a string OR a message array. We always send the message
        // array form so the system prompt + conversation history is preserved (verified
        // empirically against the live API on 2026-07-31).
        var apiRequest = new ResponsesApiRequest
        {
            Model = _modelId,
            Input = request.Messages
                .Select(m => new InputMessage(m.Role, m.Content))
                .ToArray(),
            // Tier 1/2 (flash) use the Responses API precisely to control reasoning spend.
            // effort: none skips reasoning entirely; low/medium/high scale the reasoning budget.
            Reasoning = new ReasoningConfig { Effort = _reasoningEffort },
            MaxOutputTokens = Math.Max(request.MaxOutputTokens, 2048),
            Temperature = request.Temperature,
            Stream = false,
        };
        // The endpoint is {base}/responses — base already ends in /v1 (default), and the
        // Responses API path is /v1/responses, so relative "responses" resolves correctly.
        var endpoint = $"{_config.DeepSeekBaseUrl.TrimEnd('/')}/responses";
        return (endpoint, JsonContent.Create(apiRequest, DeepSeekJsonContext.Default.ResponsesApiRequest));
    }

    private async Task<(string content, TokenUsage usage)> ParseResponsesAsync(Stream stream, CancellationToken ct)
    {
        var payload = await JsonSerializer.DeserializeAsync(
            stream, DeepSeekJsonContext.Default.ResponsesApiResponse, ct)
            ?? throw new InvalidOperationException("DeepSeek Responses API returned empty response");

        // output[] holds message items; each item's content[] has typed entries.
        // We collect output_text into `content` and reasoning_text separately as a fallback.
        var sb = new System.Text.StringBuilder();
        string? reasoningText = null;
        foreach (var item in payload.Output ?? [])
            foreach (var c in item.Content ?? [])
            {
                if (c.Type == "output_text")
                    sb.Append(c.Text);
                else if (c.Type == "reasoning_text")
                    reasoningText = (reasoningText ?? "") + c.Text;
            }

        var u = payload.Usage;
        var cached = u?.InputTokensDetails?.CachedTokens ?? 0;
        var newIn = (u?.InputTokens ?? 0) - cached;
        var usage = new TokenUsage
        {
            NewInputTokens = newIn,
            CachedTokens = cached,
            OutputTokens = u?.OutputTokens ?? 0,
            ReasoningTokens = u?.OutputTokensDetails?.ReasoningTokens ?? 0,
            FinishReason = payload.Status,  // "completed" / "incomplete"
            ReasoningText = reasoningText,
        };
        return (sb.ToString(), usage);
    }

    // ---- Chat Completions request/response (Tier 3: pro) ----

    private (string endpoint, JsonContent payload) BuildChatCall(LlmRequest request)
    {
        // deepseek-reasoner's max_tokens budget covers BOTH thinking tokens AND the answer.
        // 2048 is exhausted entirely by the reasoning chain — the model never reaches the
        // answer, so content comes back null. Use at least 8192 for the reasoner model.
        var effectiveMaxTokens = _modelId == "deepseek-v4-pro"
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
        var endpoint = $"{_config.DeepSeekBaseUrl.TrimEnd('/')}/chat/completions";
        return (endpoint, JsonContent.Create(apiRequest, DeepSeekJsonContext.Default.ChatCompletionRequest));
    }

    private async Task<(string content, TokenUsage usage)> ParseChatAsync(Stream stream, CancellationToken ct)
    {
        var payload = await JsonSerializer.DeserializeAsync(
            stream, DeepSeekJsonContext.Default.ChatCompletionResponse, ct)
            ?? throw new InvalidOperationException("DeepSeek returned empty response");

        var choice = payload.Choices.FirstOrDefault();
        var content = choice?.Message.Content;

        // DeepSeek-R1 (reasoner) returns "content": null for hard problems where the
        // answer is only in the internal reasoning chain (reasoning_content).
        // Fall back to reasoning_content so ExtractLeanFromResponse can still find code.
        string? reasoningText = null;
        if (string.IsNullOrEmpty(content) && !string.IsNullOrEmpty(choice?.Message.ReasoningContent))
            reasoningText = choice!.Message.ReasoningContent;

        var u = payload.Usage;
        var cached = u?.PromptCacheHitTokens ?? 0;
        var newIn = (u?.PromptTokens ?? 0) - cached;
        var usage = new TokenUsage
        {
            NewInputTokens = newIn,
            CachedTokens = cached,
            OutputTokens = u?.CompletionTokens ?? 0,
            ReasoningTokens = 0,  // chat API doesn't split reasoning tokens in usage
            FinishReason = choice?.FinishReason,
            ReasoningText = reasoningText,
        };
        return (content ?? "", usage);
    }

    // ---- shared retry wrapper ----

    /// <summary>
    /// Sends a JSON POST, retrying transient failures (429, 5xx, network errors, timeouts)
    /// with exponential backoff + jitter. Non-transient status codes (4xx other than 429)
    /// are returned to the caller unchanged so the router can latch the circuit breaker on
    /// 401/402. An <see cref="HttpRequestMessage"/> cannot be sent twice, so we rebuild it
    /// per attempt. Shared by both the Responses and Chat Completions paths.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        string endpoint, HttpContent payload, CancellationToken ct)
    {
        const int maxAttempts = 4;
        var rng = Random.Shared;
        Exception? lastTransient = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            // Fresh message per attempt — HttpRequestMessage is single-use. We must
            // re-serialize the payload because HttpContent is also single-use after send.
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.DeepSeekApiKey);
            req.Content = await CloneContentAsync(payload, ct);

            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;  // caller cancelled — don't retry
            }
            catch (Exception ex) when (IsTransientNetwork(ex))
            {
                // Timeout / connection reset / DNS — retryable.
                lastTransient = ex;
                if (attempt < maxAttempts)
                {
                    var delay = RetryBackoffSec[attempt] + rng.NextDouble();
                    _log.LogWarning("DeepSeek transient network error on {Ep} (attempt {A}/{M}), retrying in {D:F1}s: {Msg}",
                        endpoint, attempt, maxAttempts, delay, ex.Message);
                    await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                    continue;
                }
                break;
            }

            // 429 and 5xx are retryable; everything else (incl. 401/402) returned as-is.
            if ((int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500)
            {
                if (attempt < maxAttempts)
                {
                    var delay = RetryBackoffSec[attempt] + rng.NextDouble();
                    _log.LogWarning("DeepSeek {Status} on {Ep} (attempt {A}/{M}), retrying in {D:F1}s",
                        resp.StatusCode, endpoint, attempt, maxAttempts, delay);
                    resp.Dispose();
                    await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                    continue;
                }
                return resp;  // last attempt failed transiently — surface it
            }

            return resp;
        }

        // Exhausted retries on a network exception.
        throw new HttpRequestException(
            $"DeepSeek request to {endpoint} failed after {maxAttempts} transient retries", lastTransient);
    }

    /// <summary>HttpContent is single-use after SendAsync, so each retry needs a fresh copy.
    /// Reads the original payload into a buffer and wraps it in a new StringContent.</summary>
    private static async Task<HttpContent> CloneContentAsync(HttpContent original, CancellationToken ct)
    {
        var bytes = await original.ReadAsByteArrayAsync(ct);
        var clone = new ByteArrayContent(bytes);
        clone.Headers.ContentType = original.Headers.ContentType;
        return clone;
    }

    /// <summary>
    /// Classifies an exception as a transient network failure worth retrying.
    /// <see cref="TaskCanceledException"/> from <see cref="HttpClient"/> surfaces
    /// client-side timeouts (when the token is not actually cancelled).
    /// </summary>
    private static bool IsTransientNetwork(Exception ex) =>
        ex is TaskCanceledException or TimeoutException
            or System.Net.Sockets.SocketException;

    /// <summary>Internal intermediate type so both parse paths return the same shape.</summary>
    private sealed record TokenUsage
    {
        public int NewInputTokens { get; init; }
        public int CachedTokens { get; init; }
        public int OutputTokens { get; init; }
        public int ReasoningTokens { get; init; }
        public string? FinishReason { get; init; }
        public string? ReasoningText { get; init; }
    }
}

// ---- Responses API JSON contract ----

internal sealed record ResponsesApiRequest
{
    [JsonPropertyName("model")]              public required string Model { get; init; }
    [JsonPropertyName("input")]              public required InputMessage[] Input { get; init; }
    [JsonPropertyName("reasoning")]          public ReasoningConfig? Reasoning { get; init; }
    [JsonPropertyName("max_output_tokens")]  public int MaxOutputTokens { get; init; }
    [JsonPropertyName("temperature")]        public double Temperature { get; init; }
    [JsonPropertyName("stream")]             public bool Stream { get; init; }
}

internal sealed record InputMessage(
    [property: JsonPropertyName("role")]    string Role,
    [property: JsonPropertyName("content")] string Content);

internal sealed record ReasoningConfig
{
    // none | low | medium | high. none → 0 reasoning tokens (verified 2026-07-31).
    [JsonPropertyName("effort")] public string Effort { get; init; } = "low";
}

internal sealed record ResponsesApiResponse
{
    [JsonPropertyName("id")]      public string Id { get; init; } = "";
    [JsonPropertyName("model")]   public string Model { get; init; } = "";
    [JsonPropertyName("status")]  public string? Status { get; init; }
    [JsonPropertyName("output")]  public ResponseOutputItem[]? Output { get; init; }
    [JsonPropertyName("usage")]   public ResponsesUsage? Usage { get; init; }
    [JsonPropertyName("error")]   public ResponsesError? Error { get; init; }
}

internal sealed class ResponseOutputItem
{
    [JsonPropertyName("content")] public ResponseContent[]? Content { get; init; }
}

internal sealed class ResponseContent
{
    [JsonPropertyName("type")] public string Type { get; init; } = "";      // "output_text" | "reasoning_text"
    [JsonPropertyName("text")] public string Text { get; init; } = "";
}

internal sealed record ResponsesUsage
{
    [JsonPropertyName("input_tokens")]  public int InputTokens { get; init; }
    [JsonPropertyName("output_tokens")] public int OutputTokens { get; init; }
    [JsonPropertyName("total_tokens")]  public int TotalTokens { get; init; }
    [JsonPropertyName("input_tokens_details")]  public InputDetails? InputTokensDetails { get; init; }
    [JsonPropertyName("output_tokens_details")] public OutputDetails? OutputTokensDetails { get; init; }
}

internal sealed record InputDetails
{
    [JsonPropertyName("cached_tokens")] public int CachedTokens { get; init; }
}

internal sealed record OutputDetails
{
    [JsonPropertyName("reasoning_tokens")] public int ReasoningTokens { get; init; }
}

internal sealed record ResponsesError
{
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("type")]    public string? Type { get; init; }
    [JsonPropertyName("code")]    public string? Code { get; init; }
}

// ---- Chat Completions JSON contract (Tier 3: pro) ----

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

[JsonSerializable(typeof(ResponsesApiRequest))]
[JsonSerializable(typeof(ResponsesApiResponse))]
[JsonSerializable(typeof(ChatCompletionRequest))]
[JsonSerializable(typeof(ChatCompletionResponse))]
[JsonSerializable(typeof(AssistantMessage))]
internal sealed partial class DeepSeekJsonContext : JsonSerializerContext;
