using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexusAgent.Core.Agent;
using NexusAgent.Core.Configuration;
using NexusAgent.Core.Encoding;
using NexusAgent.Core.Llm;
using NexusAgent.Core.Memory;
using NexusAgent.Core.Models;
using NexusAgent.Core.Oracle;
using NexusAgent.Core.Planning;
using NexusAgent.Core.Prompts;
using NexusAgent.Core.Safety;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var builder = Host.CreateApplicationBuilder(args);

// Bind appsettings.json, then overlay environment variables (DEEPSEEK_API_KEY, NEXUS_*)
builder.Services.Configure<NexusConfig>(cfg =>
{
    builder.Configuration.GetSection("Nexus").Bind(cfg);
    cfg.ApplyEnvironmentOverrides();
});

// --- HTTP clients ---
builder.Services.AddHttpClient<QwenLocalClient>(c => c.Timeout = TimeSpan.FromMinutes(5));
builder.Services.AddHttpClient<DeepSeekClient>(c => c.Timeout = TimeSpan.FromMinutes(5));

// --- LLM clients (registered as ILlmClient via factories) ---
builder.Services.AddSingleton<ILlmClient>(sp => sp.GetRequiredService<QwenLocalClient>());
builder.Services.AddSingleton<ILlmClient>(sp =>
{
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(DeepSeekClient));
    return DeepSeekClient.Flash(http,
        sp.GetRequiredService<IOptions<NexusConfig>>(),
        sp.GetRequiredService<ILogger<DeepSeekClient>>());
});
builder.Services.AddSingleton<ILlmClient>(sp =>
{
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(DeepSeekClient));
    return DeepSeekClient.Pro(http,
        sp.GetRequiredService<IOptions<NexusConfig>>(),
        sp.GetRequiredService<ILogger<DeepSeekClient>>());
});

// --- Router ---
builder.Services.AddSingleton(sp =>
{
    var cfg = sp.GetRequiredService<IOptions<NexusConfig>>().Value;
    return new RouterConfig { BudgetCapUsd = cfg.BudgetCapUsd };
});
builder.Services.AddSingleton<TieredLlmRouter>();

// --- Storage and infra ---
builder.Services.AddSingleton<INeo4jClient, Neo4jClient>();

// --- Pipeline ---
builder.Services.AddSingleton<ProofStateEncoder>();
builder.Services.AddSingleton<PromptBuilder>();
builder.Services.AddSingleton<ProofFossilizer>();
builder.Services.AddSingleton<HallucinationGate>();
builder.Services.AddSingleton<ProofCartographer>();
builder.Services.AddSingleton<ILeanOracle, LeanOracle>();
builder.Services.AddSingleton<NexusProverSubagent>();
builder.Services.AddSingleton<NexusOrchestrator>();

builder.Logging.SetMinimumLevel(LogLevel.Information);

var host = builder.Build();

var cmd = args[0].ToLowerInvariant();
var rest = args.Skip(1).ToArray();

// --- Ensure schema once on startup (skip for probe — no DB needed) ---
if (cmd != "probe")
{
    using var scope = host.Services.CreateScope();
    var neo4j = scope.ServiceProvider.GetRequiredService<INeo4jClient>();
    await neo4j.EnsureSchemaAsync(CancellationToken.None);
}

return cmd switch
{
    "solve"     => await RunSolveAsync(host, rest),
    "bench"     => await RunBenchAsync(host, rest),
    "schema"    => RunSchema(host),
    "stats"     => await RunStatsAsync(host),
    "probe"     => await RunProbeAsync(host),
    _           => UnknownCommand(cmd),
};

static int UnknownCommand(string cmd)
{
    Console.Error.WriteLine($"Unknown command: {cmd}");
    PrintUsage();
    return 1;
}

static async Task<int> RunProbeAsync(IHost host)
{
    var cfg = host.Services.GetRequiredService<IOptions<NexusConfig>>().Value;
    var log = host.Services.GetRequiredService<ILogger<Program>>();
    var http = host.Services.GetRequiredService<IHttpClientFactory>();

    Console.WriteLine("=== Nexus LLM probe ===");
    Console.WriteLine();

    // ---- Tier 1: Qwen via Ollama ----
    Console.Write($"[Tier 1] Ollama ({cfg.OllamaBaseUrl})  model={cfg.QwenModelTag} ... ");
    try
    {
        using var ollamaHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // Ollama stores models at $OLLAMA_MODELS (WD-Black SSD); the API is always localhost
        var ollamaModelsDir = Environment.GetEnvironmentVariable("OLLAMA_MODELS") ?? "(default)";
        var tagsResp = await ollamaHttp.GetAsync($"{cfg.OllamaBaseUrl.TrimEnd('/')}/api/tags");
        if (tagsResp.IsSuccessStatusCode)
        {
            var body = await tagsResp.Content.ReadAsStringAsync();
            var hasModel = body.Contains(cfg.QwenModelTag.Split(':')[0], StringComparison.OrdinalIgnoreCase);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(hasModel ? "OK" : "OK (daemon up, model may not be pulled)");
            Console.ResetColor();
            Console.WriteLine($"         OLLAMA_MODELS = {ollamaModelsDir}");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FAIL ({tagsResp.StatusCode})");
            Console.ResetColor();
        }
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"FAIL ({ex.Message})");
        Console.ResetColor();
    }

    // ---- Tier 2/3: DeepSeek API ----
    Console.Write($"[Tier 2/3] DeepSeek ({cfg.DeepSeekBaseUrl})  key={MaskKey(cfg.DeepSeekApiKey)} ... ");
    if (string.IsNullOrWhiteSpace(cfg.DeepSeekApiKey))
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("SKIP (no DEEPSEEK_API_KEY set)");
        Console.ResetColor();
    }
    else
    {
        try
        {
            using var dsHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            dsHttp.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cfg.DeepSeekApiKey);
            var modelsResp = await dsHttp.GetAsync($"{cfg.DeepSeekBaseUrl.TrimEnd('/')}/models");
            Console.ForegroundColor = modelsResp.IsSuccessStatusCode ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine(modelsResp.IsSuccessStatusCode ? "OK" : $"FAIL ({modelsResp.StatusCode})");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FAIL ({ex.Message})");
            Console.ResetColor();
        }
    }

    Console.WriteLine();
    Console.WriteLine($"Budget cap:   ${cfg.BudgetCapUsd:F0}");
    Console.WriteLine($"Tactic vocab: {cfg.TacticVocabPath}");
    Console.WriteLine($"Lean project: {(string.IsNullOrEmpty(cfg.LeanProjectPath) ? "(not set — set NEXUS_LEAN_PROJECT)" : cfg.LeanProjectPath)}");
    return 0;
}

static string MaskKey(string key) =>
    key.Length < 8 ? "(empty)" : key[..8] + "...";

static void PrintUsage()
{
    Console.WriteLine(
        """
        nexus — Rich Learning Nexus Agent

        Usage:
          nexus probe                           ← test Qwen (Ollama) + DeepSeek connectivity
          nexus solve <problem.lean> --id <id> --domain <tag> [--statement <txt>]
          nexus bench <problem-dir>  --source OEIS|Erdos
          nexus schema                          ← print Neo4j DDL to stdout
          nexus stats                           ← show fossil/landmark counts

        Environment variables:
          DEEPSEEK_API_KEY        DeepSeek V4 API key (shared with FSDE)
          NEXUS_NEO4J_PASSWORD    Neo4j password
          NEXUS_NEO4J_URI         Neo4j bolt URI        (default: bolt://localhost:7687)
          NEXUS_LEAN_PROJECT      Path to NexusLean project root
          NEXUS_QWEN_MODEL        Ollama model tag      (default: qwen3.6:35b-a3b)
          NEXUS_OLLAMA_URL        Ollama base URL       (default: http://localhost:11434)
          NEXUS_BUDGET_USD        Spend cap in USD      (default: 200)
          OLLAMA_MODELS           Read by Ollama daemon — WD-Black SSD path

        Examples:
          nexus probe
          nexus solve ./NexusLean/Problems/OEIS/A123456.lean --id OEIS_A123456 --domain combinatorics
          nexus bench ./NexusLean/Problems/OEIS --source OEIS
        """);
}

static async Task<int> RunSolveAsync(IHost host, string[] args)
{
    var file = args.FirstOrDefault();
    if (file is null || !File.Exists(file))
    {
        Console.Error.WriteLine("Missing or invalid problem file");
        return 1;
    }
    var id = GetFlag(args, "--id") ?? Path.GetFileNameWithoutExtension(file);
    var domain = GetFlag(args, "--domain") ?? "other";
    var statement = GetFlag(args, "--statement") ?? "(see file)";
    var sketch = await File.ReadAllTextAsync(file);

    var orchestrator = host.Services.GetRequiredService<NexusOrchestrator>();
    var input = new ProblemInput(id, "Manual", domain, file, statement, sketch);
    var config = new OrchestratorConfig();

    var result = await orchestrator.SolveAsync(input, config, CancellationToken.None);
    PrintResult(result);
    return result.Outcome == ProofOutcome.Solved ? 0 : 2;
}

static async Task<int> RunBenchAsync(IHost host, string[] args)
{
    var dir = args.FirstOrDefault();
    if (dir is null || !Directory.Exists(dir))
    {
        Console.Error.WriteLine("Missing or invalid problem directory");
        return 1;
    }
    var source = GetFlag(args, "--source") ?? "OEIS";

    var orchestrator = host.Services.GetRequiredService<NexusOrchestrator>();
    var router = host.Services.GetRequiredService<TieredLlmRouter>();
    var log = host.Services.GetRequiredService<ILogger<Program>>();

    var files = Directory.GetFiles(dir, "*.lean", SearchOption.TopDirectoryOnly);
    log.LogInformation("Benchmark run: {N} problems from {Dir} ({Source})", files.Length, dir, source);

    var results = new List<ProofResult>();
    foreach (var file in files.OrderBy(f => f))
    {
        var id = $"{source}_{Path.GetFileNameWithoutExtension(file)}";
        var sketch = await File.ReadAllTextAsync(file);
        var domain = source.Equals("OEIS", StringComparison.OrdinalIgnoreCase) ? "combinatorics" : "other";
        var input = new ProblemInput(id, source, domain, file, "(see file)", sketch);
        var config = new OrchestratorConfig();

        log.LogInformation("--- Starting {Id} (budget remaining: ${Rem:F2}) ---",
            id, router.RemainingBudgetUsd);

        var result = await orchestrator.SolveAsync(input, config, CancellationToken.None);
        results.Add(result);

        if (router.RemainingBudgetUsd <= 0)
        {
            log.LogWarning("Budget exhausted; stopping benchmark");
            break;
        }
    }

    PrintBenchmarkSummary(results, router.SpentUsd);
    return 0;
}

static int RunSchema(IHost host)
{
    var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "neo4j_schema.cypher");
    if (File.Exists(path)) Console.WriteLine(File.ReadAllText(path));
    else Console.WriteLine("// docs/neo4j_schema.cypher not found; schema auto-applied on startup");
    return 0;
}

static async Task<int> RunStatsAsync(IHost host)
{
    var neo4j = host.Services.GetRequiredService<INeo4jClient>();
    var count = await neo4j.CountFossilsAsync(CancellationToken.None);
    Console.WriteLine($"Proof fossils in vault: {count}");
    return 0;
}

static void PrintResult(ProofResult r)
{
    Console.WriteLine();
    Console.WriteLine($"  Problem:         {r.ProblemId}");
    Console.WriteLine($"  Outcome:         {r.Outcome}");
    Console.WriteLine($"  Episodes used:   {r.EpisodesUsed}");
    Console.WriteLine($"  Turns used:      {r.TurnsUsed}");
    Console.WriteLine($"  Fossil hits:     {r.FossilHits}");
    Console.WriteLine($"  LLM calls:       Qwen={r.LlmCallsTier1}  Flash={r.LlmCallsTier2}  Pro={r.LlmCallsTier3}");
    Console.WriteLine($"  Estimated cost:  ${r.EstimatedCostUsd:F4}");
    Console.WriteLine($"  Duration:        {r.TotalDuration.TotalMinutes:F1} min");
}

static void PrintBenchmarkSummary(List<ProofResult> results, decimal totalSpent)
{
    Console.WriteLine();
    Console.WriteLine("=== Benchmark summary ===");
    Console.WriteLine($"Problems attempted: {results.Count}");
    Console.WriteLine($"Solved:             {results.Count(r => r.Outcome == ProofOutcome.Solved)}");
    Console.WriteLine($"Budget exhausted:   {results.Count(r => r.Outcome == ProofOutcome.EpisodeBudgetExhausted)}");
    Console.WriteLine($"Timed out:          {results.Count(r => r.Outcome == ProofOutcome.TimedOut)}");
    Console.WriteLine($"Total fossil hits:  {results.Sum(r => r.FossilHits)}");
    Console.WriteLine($"Total spend:        ${totalSpent:F2}");
}

static string? GetFlag(string[] args, string name)
{
    var idx = Array.IndexOf(args, name);
    if (idx < 0 || idx + 1 >= args.Length) return null;
    return args[idx + 1];
}
