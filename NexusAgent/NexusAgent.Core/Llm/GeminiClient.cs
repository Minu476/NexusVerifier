using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexusAgent.Core.Configuration;
using NexusAgent.Core.Models;

namespace NexusAgent.Core.Llm;

/// <summary>
/// Google Gemini client. Uses the OpenAI-compatible endpoint from Google AI Studio.
/// Tagged as <see cref="LlmTier.Tier0_GateJuror"/> — used exclusively by
/// <c>HallucinationGate</c> for majority-vote lemma classification.
/// Not part of the main proof-generation tier ladder.
///
/// Endpoint: https://generativelanguage.googleapis.com/v1beta/openai
/// Model:    gemini-2.5-flash  (free tier on AI Studio)
/// Auth:     Authorization: Bearer $GOOGLE_API_KEY
///
/// Pricing (AI Studio paid tier): $0.075/M input, $0.30/M output.
/// During free-credit period EstimatedCostUsd is reported as 0.
/// </summary>
public sealed class GeminiClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly NexusConfig _config;
    private readonly ILogger<GeminiClient> _log;

    public LlmTier Tier => LlmTier.Tier0_GateJuror;

    public GeminiClient(
        HttpClient http,
        IOptions<NexusConfig> config,
        ILogger<GeminiClient> log)
    {
        _http = http;
        _config = config.Value;
        _log = log;
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        var apiRequest = new ChatCompletionRequest
        {
            Model = _config.GeminiModel,
            Messages = request.Messages
                .Select(m => new ChatMessage(m.Role, m.Content))
                .ToArray(),
            Temperature = request.Temperature,
            MaxTokens = request.MaxOutputTokens,
            Stream = false,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{_config.GeminiBaseUrl.TrimEnd('/')}/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.GoogleApiKey);
        req.Content = JsonContent.Create(apiRequest, DeepSeekJsonContext.Default.ChatCompletionRequest);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _log.LogError("Gemini API error {Status}: {Body}", resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }

        var payload = await resp.Content.ReadFromJsonAsync(
            DeepSeekJsonContext.Default.ChatCompletionResponse, ct)
            ?? throw new InvalidOperationException("Gemini returned empty response");

        sw.Stop();

        var content = payload.Choices.FirstOrDefault()?.Message.Content ?? "";

        _log.LogDebug(
            "Gemini {Model}: {In} input, {Out} output tokens",
            _config.GeminiModel,
            payload.Usage?.PromptTokens ?? 0,
            payload.Usage?.CompletionTokens ?? 0);

        return new LlmResponse
        {
            Content = content,
            Tier = Tier,
            InputTokens = payload.Usage?.PromptTokens ?? 0,
            OutputTokens = payload.Usage?.CompletionTokens ?? 0,
            CachedInputTokens = 0,
            EstimatedCostUsd = 0m,  // Free credit; update to $0.075/M in / $0.30/M out after
            Latency = sw.Elapsed,
            FinishReason = payload.Choices.FirstOrDefault()?.FinishReason,
        };
    }
}
