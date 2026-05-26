using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    var results = new List<BenchRecord>();
    foreach (var file in files.OrderBy(f => f))
    {
        var id = $"{source}_{Path.GetFileNameWithoutExtension(file)}";
        var sketch = await File.ReadAllTextAsync(file);
        var domain = ExtractDomain(sketch, source);
        var statement = ExtractStatement(sketch);
        var input = new ProblemInput(id, source, domain, file, statement, sketch);
        var config = new OrchestratorConfig();

        log.LogInformation("--- Starting {Id} (domain={Domain}, budget remaining: ${Rem:F2}) ---",
            id, domain, router.RemainingBudgetUsd);

        var started = DateTime.UtcNow;
        var result = await orchestrator.SolveAsync(input, config, CancellationToken.None);
        results.Add(new BenchRecord(id, domain, statement, result));

        PrintResult(result);

        if (router.RemainingBudgetUsd <= 0)
        {
            log.LogWarning("Budget exhausted; stopping benchmark");
            break;
        }
    }

    PrintBenchmarkSummary(results.Select(r => r.Result).ToList(), router.SpentUsd);

    // Write JSON + HTML artifacts
    var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
    var outDir = Path.Combine(dir, "..", "results");
    Directory.CreateDirectory(outDir);
    var jsonPath = Path.Combine(outDir, $"bench-{timestamp}.json");
    var htmlPath = Path.Combine(outDir, $"bench-{timestamp}.html");

    await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    await File.WriteAllTextAsync(htmlPath, BuildHtmlReport(results, source, dir, router.SpentUsd, timestamp));

    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"Results saved:");
    Console.WriteLine($"  JSON: {jsonPath}");
    Console.WriteLine($"  HTML: {htmlPath}");
    Console.ResetColor();
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

// ─── Phase 8 helpers ─────────────────────────────────────────────────────────

/// <summary>
/// Extract the primary domain tag from AMS classification in the Lean file.
/// AMS 05 = Combinatorics, 11 = Number theory, 12-20 = Algebra, 26-49 = Analysis.
/// </summary>
static string ExtractDomain(string fileContent, string source)
{
    var m = Regex.Match(fileContent, @"AMS\s+(\d+)", RegexOptions.IgnoreCase);
    if (m.Success && int.TryParse(m.Groups[1].Value, out var amsCode))
    {
        return amsCode switch
        {
              5 or  6 => "combinatorics",
             11       => "number_theory",
            >= 12 and <= 20 => "algebra",
            >= 26 and <= 49 => "analysis",
            _ => "other",
        };
    }
    return source.Equals("OEIS", StringComparison.OrdinalIgnoreCase) ? "number_theory" : "other";
}

/// <summary>
/// Extract the human-readable statement from the Lean module docstring (/-! ... -/).
/// Returns the first non-empty content line after the -/ header.
/// </summary>
static string ExtractStatement(string fileContent)
{
    var m = Regex.Match(fileContent, @"/-!(.*?)-/", RegexOptions.Singleline);
    if (!m.Success) return "(see file)";

    var lines = m.Groups[1].Value
        .Split('\n')
        .Select(l => l.Trim())
        .Where(l => l.Length > 0 && !l.StartsWith('#'))
        .Take(3)
        .ToArray();

    return lines.Length > 0 ? string.Join(" ", lines) : "(see file)";
}

static string BuildHtmlReport(
    List<BenchRecord> records,
    string source,
    string dir,
    decimal totalSpent,
    string timestamp)
{
    var solved = records.Count(r => r.Result.Outcome == ProofOutcome.Solved);
    var total = records.Count;
    var solveRate = total > 0 ? (100.0 * solved / total) : 0;

    var rowsSb = new StringBuilder();
    foreach (var rec in records)
    {
        var r = rec.Result;
        var outcomeClass = r.Outcome switch
        {
            ProofOutcome.Solved => "solved",
            ProofOutcome.TimedOut => "timeout",
            ProofOutcome.LeanEnvironmentError => "error",
            _ => "failed",
        };
        var outcomeEmoji = r.Outcome == ProofOutcome.Solved ? "✅" : (r.Outcome == ProofOutcome.TimedOut ? "⏱️" : "❌");
        rowsSb.AppendLine($"""
            <tr class="{outcomeClass}">
              <td><code>{HtmlEsc(rec.Id)}</code></td>
              <td><span class="domain-tag">{HtmlEsc(rec.Domain)}</span></td>
              <td class="statement" title="{HtmlEsc(rec.Statement)}">{HtmlEsc(Truncate(rec.Statement, 80))}</td>
              <td class="outcome">{outcomeEmoji} {r.Outcome}</td>
              <td class="num">{r.EpisodesUsed}</td>
              <td class="num">{r.TurnsUsed}</td>
              <td class="num">{r.FossilHits}</td>
              <td class="num">{r.LlmCallsTier1}</td>
              <td class="num">{r.LlmCallsTier2}</td>
              <td class="num">{r.LlmCallsTier3}</td>
              <td class="num">${r.EstimatedCostUsd:F4}</td>
              <td class="num">{r.TotalDuration.TotalMinutes:F1} min</td>
            </tr>
            """);
    }

    return $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="UTF-8">
          <meta name="viewport" content="width=device-width, initial-scale=1.0">
          <title>NexusAgent Benchmark — {{source}} {{timestamp}}</title>
          <style>
            :root {
              --bg: #0f1117; --surface: #1a1d27; --border: #2e3248;
              --solved: #00c896; --failed: #ff5f6d; --timeout: #ffb347; --error: #a0a0a0;
              --text: #e0e4f0; --muted: #7a809c; --accent: #6e8efb;
            }
            * { box-sizing: border-box; margin: 0; padding: 0; }
            body { font-family: 'Segoe UI', system-ui, sans-serif; background: var(--bg); color: var(--text); padding: 32px; }
            h1 { font-size: 1.6rem; font-weight: 700; color: var(--accent); margin-bottom: 4px; }
            .subtitle { color: var(--muted); font-size: 0.9rem; margin-bottom: 28px; }
            .cards { display: flex; gap: 16px; flex-wrap: wrap; margin-bottom: 32px; }
            .card { background: var(--surface); border: 1px solid var(--border); border-radius: 12px; padding: 20px 28px; min-width: 160px; }
            .card-value { font-size: 2.4rem; font-weight: 800; }
            .card-label { font-size: 0.8rem; color: var(--muted); margin-top: 4px; text-transform: uppercase; letter-spacing: .05em; }
            .card.solved .card-value { color: var(--solved); }
            .card.rate .card-value { color: var(--accent); }
            .card.cost .card-value { color: var(--timeout); }
            table { width: 100%; border-collapse: collapse; background: var(--surface); border-radius: 12px; overflow: hidden; border: 1px solid var(--border); font-size: 0.85rem; }
            thead { background: #232637; }
            th { padding: 12px 10px; text-align: left; color: var(--muted); font-weight: 600; font-size: 0.75rem; text-transform: uppercase; letter-spacing: .04em; border-bottom: 1px solid var(--border); }
            td { padding: 10px 10px; border-bottom: 1px solid var(--border); vertical-align: middle; }
            tr:last-child td { border-bottom: none; }
            tr.solved td:first-child { border-left: 3px solid var(--solved); }
            tr.failed td:first-child { border-left: 3px solid var(--failed); }
            tr.timeout td:first-child { border-left: 3px solid var(--timeout); }
            tr.error td:first-child { border-left: 3px solid var(--error); }
            .num { text-align: right; font-variant-numeric: tabular-nums; color: var(--muted); }
            .outcome { font-weight: 600; }
            tr.solved .outcome { color: var(--solved); }
            tr.failed .outcome { color: var(--failed); }
            tr.timeout .outcome { color: var(--timeout); }
            tr.error .outcome { color: var(--error); }
            .domain-tag { background: #2e3248; border-radius: 4px; padding: 2px 6px; font-size: 0.72rem; color: var(--muted); }
            .statement { max-width: 320px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: var(--muted); font-size: 0.82rem; }
            code { font-family: 'JetBrains Mono', 'Fira Code', monospace; font-size: 0.8rem; color: var(--accent); }
            .footer { margin-top: 24px; color: var(--muted); font-size: 0.8rem; }
          </style>
        </head>
        <body>
          <h1>NexusAgent Benchmark Report</h1>
          <div class="subtitle">Source: {{source}} · Directory: {{HtmlEsc(dir)}} · Run: {{timestamp}}</div>

          <div class="cards">
            <div class="card solved">
              <div class="card-value">{{solved}}/{{total}}</div>
              <div class="card-label">Problems solved</div>
            </div>
            <div class="card rate">
              <div class="card-value">{{solveRate:F1}}%</div>
              <div class="card-label">Solve rate</div>
            </div>
            <div class="card">
              <div class="card-value">{{records.Sum(r => r.Result.FossilHits)}}</div>
              <div class="card-label">Fossil hits</div>
            </div>
            <div class="card cost">
              <div class="card-value">${{totalSpent:F2}}</div>
              <div class="card-label">Total API spend</div>
            </div>
            <div class="card">
              <div class="card-value">{{records.Sum(r => r.Result.EpisodesUsed)}}</div>
              <div class="card-label">Total episodes</div>
            </div>
          </div>

          <table>
            <thead>
              <tr>
                <th>Problem ID</th>
                <th>Domain</th>
                <th>Statement</th>
                <th>Outcome</th>
                <th>Episodes</th>
                <th>Turns</th>
                <th>Fossils</th>
                <th>Qwen calls</th>
                <th>Flash calls</th>
                <th>Pro calls</th>
                <th>Cost</th>
                <th>Duration</th>
              </tr>
            </thead>
            <tbody>
        {{rowsSb}}
            </tbody>
          </table>

          <div class="footer">
            Generated by NexusAgent CLI · DeepMind Nexus Challenge · {{DateTime.Now:yyyy-MM-dd HH:mm}} UTC
          </div>
        </body>
        </html>
        """;
}

static string HtmlEsc(string s) =>
    s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

static string Truncate(string s, int max) =>
    s.Length <= max ? s : s[..max] + "…";

/// <summary>Captures per-problem bench results with metadata for reporting.</summary>
record BenchRecord(string Id, string Domain, string Statement, ProofResult Result);
