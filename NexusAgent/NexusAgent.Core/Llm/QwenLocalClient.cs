using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexusAgent.Core.Configuration;
using NexusAgent.Core.Models;

namespace NexusAgent.Core.Llm;

/// <summary>
/// Local Qwen3.6-35B-A3B via Ollama. Free, fast (3B active params), used for
/// screening, tactic ranking, hallucination classification, and Cartographer
/// dead-end detection.
///
/// Ollama API: http://localhost:11434/api/chat
/// Install: `ollama pull qwen3.6:35b-a3b` (or whichever tag matches your install)
/// </summary>
public sealed class QwenLocalClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly NexusConfig _config;
    private readonly ILogger<QwenLocalClient> _log;

    public LlmTier Tier => LlmTier.Tier1_QwenLocal;

    public QwenLocalClient(
        HttpClient http,
        IOptions<NexusConfig> config,
        ILogger<QwenLocalClient> log)
    {
        _http = http;
        _config = config.Value;
        _log = log;
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        var ollamaRequest = new OllamaChatRequest
        {
            Model = _config.QwenModelTag,
            Messages = request.Messages
                .Select(m => new OllamaMessage(m.Role, m.Content))
                .ToArray(),
            Stream = false,
            Options = new OllamaOptions
            {
                Temperature = request.Temperature,
                NumPredict = request.MaxOutputTokens,
            },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{_config.OllamaBaseUrl.TrimEnd('/')}/api/chat")
        {
            Content = JsonContent.Create(ollamaRequest, OllamaJsonContext.Default.OllamaChatRequest),
        };

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        resp.EnsureSuccessStatusCode();

        var payload = await resp.Content.ReadFromJsonAsync(
            OllamaJsonContext.Default.OllamaChatResponse, ct)
            ?? throw new InvalidOperationException("Ollama returned empty response");

        sw.Stop();

        _log.LogDebug("Qwen completion: {InputTokens} in, {OutputTokens} out, {ElapsedMs}ms",
            payload.PromptEvalCount, payload.EvalCount, sw.ElapsedMilliseconds);

        return new LlmResponse
        {
            Content = payload.Message.Content,
            Tier = Tier,
            InputTokens = payload.PromptEvalCount,
            OutputTokens = payload.EvalCount,
            CachedInputTokens = 0,                 // Ollama doesn't cache across requests
            EstimatedCostUsd = 0m,                  // Local inference = free
            Latency = sw.Elapsed,
            FinishReason = payload.DoneReason,
        };
    }
}

internal sealed record OllamaChatRequest
{
    [JsonPropertyName("model")]      public required string Model { get; init; }
    [JsonPropertyName("messages")]   public required OllamaMessage[] Messages { get; init; }
    [JsonPropertyName("stream")]     public required bool Stream { get; init; }
    [JsonPropertyName("options")]    public OllamaOptions? Options { get; init; }
    // Disable Qwen 3.x extended thinking — thinking tokens consume num_predict
    // budget leaving no room for the actual code output (content = "").
    [JsonPropertyName("think")]      public bool Think { get; init; } = false;
}

internal sealed record OllamaMessage(
    [property: JsonPropertyName("role")]    string Role,
    [property: JsonPropertyName("content")] string Content);

internal sealed record OllamaOptions
{
    [JsonPropertyName("temperature")] public double Temperature { get; init; }
    [JsonPropertyName("num_predict")] public int NumPredict { get; init; }
}

internal sealed record OllamaChatResponse
{
    [JsonPropertyName("model")]              public string Model { get; init; } = "";
    [JsonPropertyName("message")]            public OllamaMessage Message { get; init; } = new("assistant", "");
    [JsonPropertyName("done")]               public bool Done { get; init; }
    [JsonPropertyName("done_reason")]        public string? DoneReason { get; init; }
    [JsonPropertyName("prompt_eval_count")]  public int PromptEvalCount { get; init; }
    [JsonPropertyName("eval_count")]         public int EvalCount { get; init; }
}

[JsonSerializable(typeof(OllamaChatRequest))]
[JsonSerializable(typeof(OllamaChatResponse))]
internal sealed partial class OllamaJsonContext : JsonSerializerContext;
