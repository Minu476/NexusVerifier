namespace NexusAgent.Core.Configuration;

/// <summary>
/// All-in-one configuration POCO. Bound from appsettings.json via
/// IOptions&lt;NexusConfig&gt;, then overlaid with environment variables.
///
/// Environment variables (all prefixed NEXUS_ except shared cross-project vars):
///   DEEPSEEK_API_KEY        → DeepSeekApiKey   (same var used by FSDE)
///   DASHSCOPE_API_KEY       → DashScopeApiKey  (Qwen cloud / Tier 3)
///   NEXUS_NEO4J_URI         → Neo4jUri         (fallback: NEO4J_URI from FSDE)
///   NEXUS_NEO4J_USER        → Neo4jUser        (fallback: NEO4J_USERNAME from FSDE)
///   NEXUS_NEO4J_PASSWORD    → Neo4jPassword    (fallback: NEO4J_PASSWORD from FSDE)
///   NEXUS_NEO4J_DATABASE    → Neo4jDatabase    (default: nexusdb)
///   NEXUS_LEAN_PROJECT      → LeanProjectPath
///   NEXUS_OLLAMA_URL        → OllamaBaseUrl    (default: http://localhost:11434)
///   NEXUS_QWEN_MODEL        → QwenModelTag     (default: qwen3.7:35b-a3b)
///   NEXUS_QWEN_CLOUD_MODEL  → QwenCloudModelTag (default: qwen-max)
///   NEXUS_DASHSCOPE_BASE_URL → DashScopeBaseUrl
///   NEXUS_DEEPSEEK_BASE_URL → DeepSeekBaseUrl  (default: https://api.deepseek.com/v1)
///   NEXUS_BUDGET_USD        → BudgetCapUsd
///   NEXUS_TACTIC_VOCAB      → TacticVocabPath
///
/// OLLAMA_MODELS env var (e.g. /Volumes/WD-Black/Ollama-Models) is read by the
/// Ollama daemon itself — nothing needed here; the API endpoint stays localhost:11434.
///
/// See ENHANCEMENT_GUIDE.md for tuning advice.
/// </summary>
public sealed class NexusConfig
{
    // ---- Neo4j ----
    public string Neo4jUri { get; set; } = "bolt://localhost:7687";
    public string Neo4jUser { get; set; } = "neo4j";
    public string Neo4jPassword { get; set; } = "";
    public string Neo4jDatabase { get; set; } = "nexusdb";

    // ---- Lean ----
    public string LeanProjectPath { get; set; } = "";
    public TimeSpan LeanCompileTimeout { get; set; } = TimeSpan.FromSeconds(30);

    // ---- LLM: Qwen (local Ollama, Tier 1) ----
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";
    public string QwenModelTag { get; set; } = "qwen3.7:35b-a3b";

    // ---- LLM: Qwen cloud / Tier 3 (DashScope international) ----
    // Pricing: $3.23/M input, $0.32/M cached, $9.58/M output (May 2026)
    public string DashScopeBaseUrl { get; set; } = "https://dashscope-intl.aliyuncs.com/compatible-mode/v1";
    public string DashScopeApiKey { get; set; } = "";
    public string QwenCloudModelTag { get; set; } = "qwen-max";

    // ---- LLM: DeepSeek Flash / Tier 2 (cloud API) ----
    public string DeepSeekBaseUrl { get; set; } = "https://api.deepseek.com/v1";
    public string DeepSeekApiKey { get; set; } = "";

    // ---- Encoder ----
    public string TacticVocabPath { get; set; } = "data/tactics_vocab.json";

    // ---- Budget ----
    public decimal BudgetCapUsd { get; set; } = 200m;

    /// <summary>
    /// Overlay environment variable values on top of whatever appsettings.json
    /// provided. Env vars always win. Call this after IOptions binding.
    /// </summary>
    public void ApplyEnvironmentOverrides()
    {
        // DeepSeek — reuse the same var name as FSDE so one shell setup covers both projects
        DeepSeekApiKey     = Env("DEEPSEEK_API_KEY",         DeepSeekApiKey);
        DeepSeekBaseUrl    = Env("NEXUS_DEEPSEEK_BASE_URL",  DeepSeekBaseUrl);

        // DashScope / Qwen cloud (Tier 3)
        DashScopeApiKey    = Env("DASHSCOPE_API_KEY",        DashScopeApiKey);
        DashScopeBaseUrl   = Env("NEXUS_DASHSCOPE_BASE_URL", DashScopeBaseUrl);
        QwenCloudModelTag  = Env("NEXUS_QWEN_CLOUD_MODEL",   QwenCloudModelTag);

        // Neo4j — NEXUS_* vars take priority; fall back to the shared NEO4J_* vars
        // that FSDE already sets in the shell (same local Enterprise instance).
        Neo4jUri           = Env2("NEXUS_NEO4J_URI",      "NEO4J_URI",      Neo4jUri);
        Neo4jUser          = Env2("NEXUS_NEO4J_USER",     "NEO4J_USERNAME", Neo4jUser);
        Neo4jPassword      = Env2("NEXUS_NEO4J_PASSWORD", "NEO4J_PASSWORD", Neo4jPassword);
        Neo4jDatabase      = Env("NEXUS_NEO4J_DATABASE",   Neo4jDatabase);

        // Lean
        LeanProjectPath    = Env("NEXUS_LEAN_PROJECT",      LeanProjectPath);

        // Ollama / Qwen
        OllamaBaseUrl      = Env("NEXUS_OLLAMA_URL",        OllamaBaseUrl);
        QwenModelTag       = Env("NEXUS_QWEN_MODEL",        QwenModelTag);

        // Misc
        TacticVocabPath    = Env("NEXUS_TACTIC_VOCAB",      TacticVocabPath);

        if (decimal.TryParse(Environment.GetEnvironmentVariable("NEXUS_BUDGET_USD"), out var budget)
            && budget > 0)
            BudgetCapUsd = budget;
    }

    private static string Env(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name)?.Trim() is { Length: > 0 } v ? v : fallback;

    /// <summary>
    /// Checks <paramref name="primary"/> first, then <paramref name="shared"/>
    /// (the FSDE-style cross-project var), then falls back to the C# default.
    /// </summary>
    private static string Env2(string primary, string shared, string fallback) =>
        Environment.GetEnvironmentVariable(primary)?.Trim() is { Length: > 0 } v1 ? v1 :
        Environment.GetEnvironmentVariable(shared)?.Trim()  is { Length: > 0 } v2 ? v2 :
        fallback;
}
