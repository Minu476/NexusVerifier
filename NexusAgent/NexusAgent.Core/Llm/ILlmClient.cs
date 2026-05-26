using NexusAgent.Core.Models;

namespace NexusAgent.Core.Llm;

public sealed record LlmMessage(string Role, string Content);

public sealed record LlmRequest
{
    public required IReadOnlyList<LlmMessage> Messages { get; init; }
    public int MaxOutputTokens { get; init; } = 2048;
    public double Temperature { get; init; } = 0.4;
    public string? CacheKey { get; init; }  // For DeepSeek prefix-caching
}

public sealed record LlmResponse
{
    public required string Content { get; init; }
    public required LlmTier Tier { get; init; }
    public required int InputTokens { get; init; }
    public required int OutputTokens { get; init; }
    public required int CachedInputTokens { get; init; }
    public required decimal EstimatedCostUsd { get; init; }
    public required TimeSpan Latency { get; init; }
    public string? FinishReason { get; init; }
}

/// <summary>
/// Common abstraction for all LLM backends — Qwen local via Ollama, DeepSeek V4
/// Flash/Pro via OpenAI-compatible API. TieredLlmRouter dispatches to the right
/// implementation based on episode state.
/// </summary>
public interface ILlmClient
{
    LlmTier Tier { get; }
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct);
}
