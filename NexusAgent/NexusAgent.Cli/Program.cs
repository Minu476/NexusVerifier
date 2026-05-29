using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
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
using NexusAgent.VerifiedParts;             // LEGO: remove with project reference

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var builder = Host.CreateApplicationBuilder(args);

// ── Serilog ──────────────────────────────────────────────────────────────────
// Wire directly in code (no ReadFrom.Configuration assembly scanning) so it
// works reliably in Release builds. Log file lives next to the binary.
var logDir = Path.Combine(
    Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!,
    "logs");
Directory.CreateDirectory(logDir);
var logFilePath = Path.Combine(logDir, "nexus-.log");

const string consoleTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext:l} {Message:lj}{NewLine}{Exception}";
const string fileTemplate    = "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}";

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft",                           LogEventLevel.Warning)
    .MinimumLevel.Override("System",                              LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http",                     LogEventLevel.Information)
    .MinimumLevel.Override("NexusAgent",                          LogEventLevel.Debug)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: consoleTemplate, standardErrorFromLevel: LogEventLevel.Error)
    .WriteTo.File(logFilePath,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        outputTemplate: fileTemplate)
    .CreateLogger();

builder.Logging.ClearProviders();
builder.Logging.AddSerilog(Log.Logger, dispose: true);

Log.Information("Nexus starting — log file: {LogPath}", logFilePath);
// ─────────────────────────────────────────────────────────────────────────────

// Bind appsettings.json, then overlay environment variables (DEEPSEEK_API_KEY, NEXUS_*)
builder.Services.Configure<NexusConfig>(cfg =>
{
    builder.Configuration.GetSection("Nexus").Bind(cfg);
    cfg.ApplyEnvironmentOverrides();
});

// --- HTTP clients ---
builder.Services.AddHttpClient<DeepSeekClient>(c => c.Timeout = TimeSpan.FromMinutes(5));
builder.Services.AddHttpClient<GeminiClient>(c => c.Timeout = TimeSpan.FromMinutes(2));
builder.Services.AddHttpClient<QwenCloudClient>(c => c.Timeout = TimeSpan.FromMinutes(2));

// --- LLM clients — Tier 1/2/3 use DeepSeek; GeminiClient is gate juror only ---
// Tier 1: deepseek-chat, temp=0.4, exploratory early turns
builder.Services.AddSingleton<ILlmClient>(sp =>
{
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(DeepSeekClient));
    return DeepSeekClient.Tier1(http,
        sp.GetRequiredService<IOptions<NexusConfig>>(),
        sp.GetRequiredService<ILogger<DeepSeekClient>>());
});
// Tier 2: deepseek-chat, temp=0.3, focused turns after stall
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

// Gemini 2.5 Flash — Tier0_GateJuror: second voter in HallucinationGate majority vote.
// Registered only when GOOGLE_API_KEY is set; the gate gracefully falls back to
// single-model verdict when absent (Tier1_Cheap DeepSeek still votes).
var googleApiKey = Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
if (!string.IsNullOrWhiteSpace(googleApiKey))
{
    builder.Services.AddSingleton<ILlmClient>(sp =>
    {
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(GeminiClient));
        return new GeminiClient(http,
            sp.GetRequiredService<IOptions<NexusConfig>>(),
            sp.GetRequiredService<ILogger<GeminiClient>>());
    });
    Log.Information("GeminiClient registered as hallucination gate juror (GOOGLE_API_KEY found)");
}
else
{
    Log.Warning("GOOGLE_API_KEY not set — GeminiClient disabled. HallucinationGate falls back to single-model verdict.");
}

// Qwen3.7-max — Tier0_GateJuror: third voter in HallucinationGate majority vote.
// DashScope prefix-caching makes classify calls ~10× cheaper after the first per session.
// Registered only when DASHSCOPE_API_KEY is set.
var dashScopeApiKey = Environment.GetEnvironmentVariable("DASHSCOPE_API_KEY");
if (!string.IsNullOrWhiteSpace(dashScopeApiKey))
{
    builder.Services.AddSingleton<ILlmClient>(sp =>
    {
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(QwenCloudClient));
        return QwenCloudClient.GateJuror(http,
            sp.GetRequiredService<IOptions<NexusConfig>>(),
            sp.GetRequiredService<ILogger<QwenCloudClient>>());
    });
    Log.Information("QwenCloudClient registered as hallucination gate juror (DASHSCOPE_API_KEY found)");
}
else
{
    Log.Warning("DASHSCOPE_API_KEY not set — QwenCloudClient gate juror disabled.");
}

// --- Router ---
builder.Services.AddSingleton(sp =>
{
    var cfg = sp.GetRequiredService<IOptions<NexusConfig>>().Value;
    return new RouterConfig { BudgetCapUsd = cfg.BudgetCapUsd };
});
builder.Services.AddSingleton<TieredLlmRouter>();

// --- Storage and infra ---
builder.Services.AddSingleton<INeo4jClient, Neo4jClient>();
builder.Services.AddSingleton<HyperedgeIngestor>();
builder.Services.AddSingleton<HgParallelSolver>(sp =>
{
    var cfg = sp.GetRequiredService<IOptions<NexusConfig>>().Value;
    var leanProject = Path.GetFullPath(
        string.IsNullOrWhiteSpace(cfg.LeanProjectPath)
            ? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "formal-conjectures")
            : cfg.LeanProjectPath);
    var engineFile = Path.Combine(leanProject, "_nexus_tmp", "ErdosHypergraph.lean");
    return new HgParallelSolver(leanProject, engineFile,
        sp.GetRequiredService<ILogger<HgParallelSolver>>());
});

// --- Pipeline ---
builder.Services.AddSingleton<ProofStateEncoder>();
builder.Services.AddSingleton<PromptBuilder>();
builder.Services.AddSingleton<ProofFossilizer>();
builder.Services.AddSingleton<HallucinationGate>();
builder.Services.AddSingleton<ProofCartographer>();
builder.Services.AddSingleton<BestFirstGraphPlanner>();
builder.Services.AddSingleton<ILeanOracle, LeanOracle>();
builder.Services.AddSingleton<NexusProverSubagent>();
builder.Services.AddSingleton<NexusOrchestrator>();
builder.Services.AddVerifiedParts();           // LEGO: remove with project reference

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

int exitCode;
try
{
    exitCode = cmd switch
    {
        "solve"     => await RunSolveAsync(host, rest),
        "bench"     => await RunBenchAsync(host, rest),
        "schema"    => RunSchema(host),
        "stats"     => await RunStatsAsync(host),
        "probe"     => await RunProbeAsync(host),
        "ingest-hg" => await RunIngestHgAsync(host, rest),
        "scan-hg"      => await RunScanHgAsync(host, rest),
        "ingest-parts" => await VerifiedPartsPlugin.RunCommandAsync(host, rest),  // LEGO
        _              => UnknownCommand(cmd),
    };
}
catch (Exception ex)
{
    Log.Fatal(ex, "Unhandled exception — command '{Cmd}' aborted", cmd);
    exitCode = 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
return exitCode;

static async Task<int> RunScanHgAsync(IHost host, string[] args)
{
    // Runs ErdosHypergraph.lean FC100 search across N parallel shard processes.
    // Each shard self-certifies (6 neg controls) before emitting PROVED results.
    // The aggregator hard-gates: shards without "control":"ok" are discarded.
    //
    // Usage:  nexus scan-hg [--shards N] [--timeout-minutes M] [--holdout]
    //   N default: auto (min(cpu_count, ram/1.5GB, 8))
    //   M default: 5 minutes per shard process
    //   --holdout: pass NEXUS_HG_HOLDOUT=1 to Lean; proved results are stored
    //              as survivesHoldout=true in Neo4j (authoritative genuine signal)
    var shards  = int.TryParse(GetFlag(args, "--shards"), out var ns) ? ns : 8;
    var minutes = int.TryParse(GetFlag(args, "--timeout-minutes"), out var nm) ? nm : 5;
    var holdout = args.Contains("--holdout");

    var solver = host.Services.GetRequiredService<HgParallelSolver>();
    var log    = host.Services.GetRequiredService<ILogger<Program>>();

    // FC100 goal names — exact order must match fc100Decls in ErdosHypergraph.lean.
    // C# passes 0-based indices to each shard; Lean looks up by position.
    var fc100Goals = new[]
    {
        /* 00 */ "WrittenOnTheWallII.Test.petersen_size",
        /* 01 */ "WrittenOnTheWallII.GraphConjecture13.conjecture13",
        /* 02 */ "OpenQuantumProblem35.ame_3_exists",
        /* 03 */ "LychrelNumbers.eventually_palindrome_base10",
        /* 04 */ "Erdos42.example_maximal_sidon",
        /* 05 */ "Mathoverflow75792.Reachable.complexity",
        /* 06 */ "OeisA280831.hasSquareCondition_0",
        /* 07 */ "Erdos141.first_three_odd_primes",
        /* 08 */ "WrittenOnTheWallII.GraphConjecture33.conjecture33",
        /* 09 */ "MonochromaticQuantumGraph.eqSystem4_has_solution_d2",
        /* 10 */ "OeisA228828.a_two",
        /* 11 */ "Erdos399.erdos_399.variants.cambie",
        /* 12 */ "Erdos349.exists_t_for_k_disjoint_segments",
        /* 13 */ "Erdos686.erdos_686.variants.four_three",
        /* 14 */ "OpenQuantumProblem23.hasConstantOverlapSq_singleton",
        /* 15 */ "WrittenOnTheWallII.Test.house_radius",
        /* 16 */ "Erdos678.lcmInterval_lt_example3",
        /* 17 */ "Gourevitch.gourevitch_series_identity",
        /* 18 */ "WrittenOnTheWallII.GraphConjecture16.conjecture16",
        /* 19 */ "Erdos12.erdos_12.variants.erdos_sarkozy",
        /* 20 */ "Mahler32.mahler_conjecture.variants.consequence",
        /* 21 */ "DegreeSequencesTriangleFree.lemma2_d",
        /* 22 */ "Kaplansky.UnitConjecture.counterexamples.ii",
        /* 23 */ "Erdos697.erdos_697.parts.i",
        /* 24 */ "Erdos61.erdos_61.variants.bnss23",
        /* 25 */ "Erdos968.erdos_968.variants.sum_abs_diff_isTheta_log_sq",
        /* 26 */ "RamanujanTau.ramanujan_petersson",
        /* 27 */ "Green14.green_14_quadratic",
        /* 28 */ "Erdos1063.erdos_1063.variants.cambie_upper_bound",
        /* 29 */ "WrittenOnTheWallII.Test.petersen_residue",
        /* 30 */ "Erdos697.density_exists",
        /* 31 */ "Green14.W_3_15",
        /* 32 */ "OpenQuantumProblem35.ame_2_exists",
        /* 33 */ "Erdos392.erdos_392.variants.implication",
        /* 34 */ "Erdos886.erdos_886.variants.rosenfeld_infinite",
        /* 35 */ "OpenQuantumProblem23.qubitSICFamily_pairwise",
        /* 36 */ "Erdos835.johnsonGraph_18_9_chromaticNumber",
        /* 37 */ "OeisA56777.a_65",
        /* 38 */ "Erdos41.erdos_41.variants.pairwise",
        /* 39 */ "OeisA232174.hasPrimeRepresentation_2",
        /* 40 */ "Erdos350.distinctSubsetSums_1_2",
        /* 41 */ "Arxiv.«2602.05192».finiteAdditiveConvolution_monic'",
        /* 42 */ "Erdos295.erdos_295.variants.erdos_straus",
        /* 43 */ "Erdos36.M_four",
        /* 44 */ "Erdos590.erdos_590",
        /* 45 */ "Arxiv.«1308.0994».KTExtendsK",
        /* 46 */ "Erdos198.erdos_198.variants.concrete",
        /* 47 */ "Erdos513.erdos_513.variants.lower_bound",
        /* 48 */ "Erdos198.baumgartner_strong",
        /* 49 */ "Erdos617.erdos_617.variants.r_eq_4",
        /* 50 */ "Erdos1038.erdos_1038.parts.ii",
        /* 51 */ "OeisA231201.primeCondition_8",
        /* 52 */ "Erdos56.maxWeaklyDivisible_one",
        /* 53 */ "Erdos17.isClusterPrime_97_isLeast_non_cluster",
        /* 54 */ "BealConjecture.flt_of_beal_conjecture",
        /* 55 */ "Arxiv.«1609.08688».maximalLength_ge_of_isSquare",
        /* 56 */ "RiemannZetaValues.infinite_irrational_at_odd",
        /* 57 */ "AgohGiuga.isWeakGiuga_iff_sum_primeFactors",
        /* 58 */ "OeisA6697.count_false_morphism",
        /* 59 */ "Mathoverflow10799.μ_half_eq_uniform",
        /* 60 */ "OeisA67720.a_6",
        /* 61 */ "OpenQuantumProblem13.Qubit.star_smul_mul_smul",
        /* 62 */ "Erdos920.erdos_920.variants.k_eq_3",
        /* 63 */ "Erdos26.erdos_26.variants.rusza",
        /* 64 */ "InverseGalois.inverse_galois_problem.variants.symmetric_group",
        /* 65 */ "Erdos1067.erdos_1067.variants.infinite_edge_connectivity",
        /* 66 */ "Erdos1074.erdos_1074.variants.EHSNumbers_init",
        /* 67 */ "Erdos26.not_isThick_of_finite",
        /* 68 */ "Erdos50.erdos_50_schoenberg",
        /* 69 */ "Erdos951.erdos_951.variants.isBeurlingPrimes",
        /* 70 */ "Erdos965.erdos_965.variants.generalization",
        /* 71 */ "Erdos985.erdos_985.variants.two_three_five_primitive_root",
        /* 72 */ "PellNumbers.pellNumber_two",
        /* 73 */ "WrittenOnTheWallII.Test.C6_size",
        /* 74 */ "BusyBeaver.sanity_check",
        /* 75 */ "OpenQuantumProblem13.Qubit.firstCol_normSq",
        /* 76 */ "Erdos263.erdos_263.variants.sub_doubly_exponential",
        /* 77 */ "Arxiv.«1609.08688».tripleProduct_const",
        /* 78 */ "Erdos317.erdos_317.variants.counterexample",
        /* 79 */ "OpenQuantumProblem23.sicOverlapSq_three",
        /* 80 */ "Erdos442.erdos_442.variants.tao",
        /* 81 */ "AgohGiuga.korselts_criterion",
        /* 82 */ "Erdos1054.f_undefined_at_2",
        /* 83 */ "Erdos503.erdos_503.variants.R3",
        /* 84 */ "OeisA63880.a_of_primitive_mul_squarefree",
        /* 85 */ "Erdos1142.erdos_1142.variants.mientka_weitzenkamp",
        /* 86 */ "CongruentNumber.congruentNumber_7",
        /* 87 */ "WrittenOnTheWallII.Test.petersen_szeged",
        /* 88 */ "WrittenOnTheWallII.Test.petersen_radius",
        /* 89 */ "Erdos590.erdos_590.variants.ge_three_false",
        /* 90 */ "Erdos494.erdos_494.variants.product",
        /* 91 */ "Green29.green_29.variant",
        /* 92 */ "Erdos865.erdos_865.variants.k2",
        /* 93 */ "Hadamard.HadamardConjecture.variants.first_cases",
        /* 94 */ "Mathoverflow10799.boundaryCount_univ",
        /* 95 */ "Erdos457.erdos_457",
        /* 96 */ "OpenQuantumProblem23.bb84Family_not_isSICFamily",
        /* 97 */ "Green32.hasGap_empty",
        /* 98 */ "Erdos1052.isUnitaryPerfect_60",
        /* 99 */ "OeisA67720.a_1",
    };

    log.LogInformation("[scan-hg] {N} goals, {S} shards, {M}m timeout{H}",
        fc100Goals.Length, shards, minutes, holdout ? ", HOLDOUT" : "");

    if (holdout)
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine("  [HOLDOUT MODE] NEXUS_HG_HOLDOUT=1 — seed graph = generic Mathlib only.");
        Console.WriteLine("  Proved results will be stored as survivesHoldout=true in Neo4j.");
        Console.ResetColor();
    }

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var summary = await solver.ScanAsync(
        fc100Goals,
        shardCount: shards,
        processTimeout: TimeSpan.FromMinutes(minutes),
        holdout: holdout,
        ct: CancellationToken.None);
    sw.Stop();

    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"══ scan-hg Summary ({sw.Elapsed.TotalSeconds:F1}s) ═════════════");
    Console.ResetColor();

    if (summary.AnyDiscarded)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  ⚠ {summary.DiscardedShards}/{summary.TotalShards} shards DISCARDED (soundness gate or timeout)");
        Console.ResetColor();
    }

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"  GENUINE  : {summary.GenuineCount} / {fc100Goals.Length}  (composed from external lemmas)");
    Console.ResetColor();
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"  SELF-CITE: {summary.SelfCiteCount} / {fc100Goals.Length}  (goal applied to itself — run SorryAudit to check sorry-free)");
    Console.ResetColor();
    Console.WriteLine($"  GAP      : {summary.GapCount} / {fc100Goals.Length}");
    Console.WriteLine($"  Shards   : {summary.TotalShards - summary.DiscardedShards} ok, {summary.DiscardedShards} discarded");
    Console.WriteLine($"  Elapsed  : {sw.Elapsed}");
    Console.WriteLine();

    foreach (var r in summary.Results.Where(r => r.Proved && !r.IsSelfCitation).OrderBy(r => r.Name))
        Console.WriteLine($"  [GENUINE]    {r.Name}  via [{string.Join(", ", r.Steps)}]");
    foreach (var r in summary.Results.Where(r => r.IsSelfCitation).OrderBy(r => r.Name))
        Console.WriteLine($"  [SELF-CITE]  {r.Name}");

    // ── Persist to Neo4j ────────────────────────────────────────────────────
    var scanRun = new HgScanRun(
        Id:              Guid.NewGuid().ToString("N"),
        RunAt:           DateTime.UtcNow - sw.Elapsed,
        ElapsedSeconds:  sw.Elapsed.TotalSeconds,
        Shards:          shards,
        TimeoutSeconds:  minutes * 60,
        TotalGoals:      fc100Goals.Length,
        ProvedCount:     summary.ProvedCount,
        GenuineCount:    summary.GenuineCount,
        SelfCiteCount:   summary.SelfCiteCount,
        GapCount:        summary.GapCount,
        DiscardedShards: summary.DiscardedShards,
        Goals:           summary.Results,
        IsHoldoutRun:    holdout);

    try
    {
        var neo4j = host.Services.GetRequiredService<INeo4jClient>();
        await neo4j.UpsertScanRunAsync(scanRun, CancellationToken.None);
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine($"  [Neo4j] :HgScanRun {scanRun.Id[..8]}…  ({summary.GenuineCount} genuine, {summary.SelfCiteCount} self-cite, {summary.GapCount} gap)");
        Console.ResetColor();
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "[scan-hg] Neo4j persistence skipped — is Neo4j running?");
    }

    return summary.AnyDiscarded ? 2 : 0;
}

static async Task<int> RunIngestHgAsync(IHost host, string[] args)
{
    // Reads hg_cache.jsonl written by the Lean cold run and upserts every
    // hyperedge into Neo4j as :HyperedgeRecord nodes.
    //
    // Usage:  nexus ingest-hg [--input <path>]
    //   default path: formal-conjectures/_nexus_tmp/hg_cache.jsonl (relative to cwd)
    var inputPath = GetFlag(args, "--input")
        ?? Path.Combine("formal-conjectures", "_nexus_tmp", "hg_cache.jsonl");

    if (!File.Exists(inputPath))
    {
        Console.Error.WriteLine($"[ingest-hg] File not found: {inputPath}");
        Console.Error.WriteLine("  Run the Lean engine cold first:");
        Console.Error.WriteLine("    cd formal-conjectures");
        Console.Error.WriteLine("    rm -f _nexus_tmp/hg_cache.hge");
        Console.Error.WriteLine("    lake env lean _nexus_tmp/ErdosHypergraph.lean");
        return 1;
    }

    var ingestor = host.Services.GetRequiredService<HyperedgeIngestor>();
    var log = host.Services.GetRequiredService<ILogger<Program>>();

    log.LogInformation("[ingest-hg] Reading {Path}", inputPath);
    var count = await ingestor.IngestAsync(inputPath, CancellationToken.None);

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"[ingest-hg] Upserted {count} :HyperedgeRecord nodes into Neo4j");
    Console.ResetColor();
    return 0;
}

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
                    nexus bench <problem-dir>  --source OEIS|Erdos [--graph-first] [--no-llm-fallback]
          nexus schema                          ← print Neo4j DDL to stdout
          nexus stats                           ← show fossil/landmark counts
          nexus ingest-hg [--input <path>]      ← push hg_cache.jsonl → Neo4j :HyperedgeRecord
          nexus scan-hg   [--shards N] [--timeout-minutes M]
                                                ← parallel FC100 search (N Lean processes)
                                                  each shard self-certifies before emitting PROVED

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
                    nexus bench ./NexusLean/Problems/OEIS --source OEIS --graph-first
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
    var source      = GetFlag(args, "--source")       ?? "OEIS";
    var maxEpisodes = int.TryParse(GetFlag(args, "--max-episodes"), out var me) ? me : 3;
    var maxTurns    = int.TryParse(GetFlag(args, "--max-turns"),    out var mt) ? mt : 8;
    var parallelism = int.TryParse(GetFlag(args, "--parallel"),     out var pl) ? pl : 4;
    var fossilMatchThreshold = float.TryParse(GetFlag(args, "--fossil-match-threshold"), out var fm) ? fm : 0.75f;
    var fossilDirectThreshold = float.TryParse(GetFlag(args, "--fossil-direct-threshold"), out var fd) ? fd : 0.70f;
    var useGraphFirstPlanner = args.Contains("--graph-first", StringComparer.OrdinalIgnoreCase);
    var useLegacyFallback = !args.Contains("--no-llm-fallback", StringComparer.OrdinalIgnoreCase);
    var plannerMaxExpansions = int.TryParse(GetFlag(args, "--planner-max-expansions"), out var pme) ? pme : 48;
    var plannerBranchFactor = int.TryParse(GetFlag(args, "--planner-branch-factor"), out var pbf) ? pbf : 8;
    var plannerNeighborK = int.TryParse(GetFlag(args, "--planner-neighbor-k"), out var pnk) ? pnk : 12;
    var plannerStateVisitCap = int.TryParse(GetFlag(args, "--planner-state-visit-cap"), out var psv) ? psv : 3;
    var plannerDepthWeight = float.TryParse(GetFlag(args, "--planner-depth-weight"), out var pdw) ? pdw : 0.10f;
    var plannerRankWeight = float.TryParse(GetFlag(args, "--planner-rank-weight"), out var prw) ? prw : 1.00f;
    var plannerSuccessWeight = float.TryParse(GetFlag(args, "--planner-success-weight"), out var psw) ? psw : 0.75f;
    var plannerBranchingWeight = float.TryParse(GetFlag(args, "--planner-branching-weight"), out var pbw) ? pbw : 0.35f;
    var plannerErrorWeight = float.TryParse(GetFlag(args, "--planner-error-weight"), out var pew) ? pew : 0.50f;
    var plannerNoveltyBonus = float.TryParse(GetFlag(args, "--planner-novelty-bonus"), out var pnb) ? pnb : 0.30f;

    var orchestrator = host.Services.GetRequiredService<NexusOrchestrator>();
    var router = host.Services.GetRequiredService<TieredLlmRouter>();
    var neo4j = host.Services.GetRequiredService<INeo4jClient>();
    var log = host.Services.GetRequiredService<ILogger<Program>>();

    var files = Directory.GetFiles(dir, "*.lean", SearchOption.TopDirectoryOnly);
    log.LogInformation("Benchmark run: {N} problems from {Dir} ({Source}), parallelism={P}",
        files.Length, dir, source, parallelism);

    var results = new ConcurrentBag<BenchRecord>();
    var budgetExhausted = false;
    var semaphore = new SemaphoreSlim(parallelism, parallelism);

    var tasks = files.OrderBy(f => f).Select(async file =>
    {
        await semaphore.WaitAsync();
        try
        {
            if (Volatile.Read(ref budgetExhausted)) return;

            var id = $"{source}_{Path.GetFileNameWithoutExtension(file)}";
            var sketch = await File.ReadAllTextAsync(file);
            var domain = ExtractDomain(sketch, source);
            var statement = ExtractStatement(sketch);

            if (await neo4j.IsProblemSolvedAsync(id, CancellationToken.None))
            {
                log.LogInformation("--- Skipping {Id} — already solved ---", id);
                return;
            }

            var input = new ProblemInput(id, source, domain, file, statement, sketch);
            var config = new OrchestratorConfig
            {
                MaxEpisodes        = maxEpisodes,
                MaxTurnsPerEpisode = maxTurns,
                FossilMatchThreshold = fossilMatchThreshold,
                FossilDirectSubstituteThreshold = fossilDirectThreshold,
                UseGraphFirstPlanner = useGraphFirstPlanner,
                UseLegacyLlmProverFallback = useLegacyFallback,
                PlannerMaxExpansions = plannerMaxExpansions,
                PlannerBranchFactor = plannerBranchFactor,
                PlannerNeighborK = plannerNeighborK,
                PlannerStateVisitCap = plannerStateVisitCap,
                PlannerDepthWeight = plannerDepthWeight,
                PlannerRankWeight = plannerRankWeight,
                PlannerSuccessWeight = plannerSuccessWeight,
                PlannerBranchingWeight = plannerBranchingWeight,
                PlannerErrorWeight = plannerErrorWeight,
                PlannerNoveltyBonus = plannerNoveltyBonus,
            };

            log.LogInformation("--- Starting {Id} (domain={Domain}, budget remaining: ${Rem:F2}) ---",
                id, domain, router.RemainingBudgetUsd);

            ProofResult result;
            try
            {
                result = await orchestrator.SolveAsync(input, config, CancellationToken.None);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Problem {Id} crashed — skipping to next", id);
                return;
            }
            results.Add(new BenchRecord(id, domain, statement, result));
            PrintResult(result);

            if (router.RemainingBudgetUsd <= 0)
            {
                log.LogWarning("Budget exhausted; no new problems will start");
                Volatile.Write(ref budgetExhausted, true);
            }
        }
        finally
        {
            semaphore.Release();
        }
    }).ToList();

    await Task.WhenAll(tasks);

    var orderedResults = results.OrderBy(r => r.Id).ToList();
    PrintBenchmarkSummary(orderedResults.Select(r => r.Result).ToList(), router.SpentUsd);

    // Write JSON + HTML artifacts
    var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
    var outDir = Path.Combine(dir, "..", "results");
    Directory.CreateDirectory(outDir);
    var jsonPath = Path.Combine(outDir, $"bench-{timestamp}.json");
    var htmlPath = Path.Combine(outDir, $"bench-{timestamp}.html");

    await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(orderedResults, new JsonSerializerOptions { WriteIndented = true }));
    await File.WriteAllTextAsync(htmlPath, BuildHtmlReport(orderedResults, source, dir, router.SpentUsd, timestamp));

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
    var analysis = await neo4j.FossilAnalysisAsync(CancellationToken.None);

    // --- Console report ---
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("=== Phase 8 — Fossil Vault Analysis ===");
    Console.ResetColor();
    Console.WriteLine();
    Console.WriteLine($"  Proof fossils:      {analysis.TotalFossils}");
    Console.WriteLine($"  Proof landmarks:    {analysis.TotalLandmarks}");
    Console.WriteLine($"  Problems solved:    {analysis.SolvedProblems}");
    Console.WriteLine($"  Cross-run hits:     {analysis.CrossRunHits}" +
        (analysis.TotalFossils > 0 ? $"  ({100.0 * analysis.CrossRunHits / analysis.TotalFossils:F1}% of vault)" : ""));
    Console.WriteLine();

    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("  Domain distribution:");
    Console.ResetColor();
    foreach (var (domain, count) in analysis.DomainDistribution.OrderByDescending(kv => kv.Value))
        Console.WriteLine($"    {domain,-24} {count} fossil(s)");
    Console.WriteLine();

    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("  Top fossils by reuse (\"universal lemmas\"):");
    Console.ResetColor();
    if (analysis.TopFossils.Count == 0)
    {
        Console.WriteLine("    (none yet — run the OEIS bench to populate)");
    }
    else
    {
        foreach (var f in analysis.TopFossils)
        {
            Console.ForegroundColor = f.UseCount > 0 ? ConsoleColor.Green : ConsoleColor.Gray;
            Console.WriteLine($"    [{f.DomainTag,-16}] uses={f.UseCount}  sorry↓{f.SorryReduction}" +
                              $"  sources={f.SourceProblems.Length}");
            Console.ResetColor();
            Console.WriteLine($"      goal:   {f.SubgoalSnippet.Replace('\n', ' ').Trim()}");
            Console.WriteLine($"      tactic: {f.TacticSnippet.Replace('\n', ' ').Trim()}");
            Console.WriteLine();
        }
    }

    if (analysis.DeepestPrecedesChains.Count > 0)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  Deepest PRECEDES chains (tactic backbone):");
        Console.ResetColor();
        foreach (var (rootId, depth) in analysis.DeepestPrecedesChains.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"    root={rootId[..12]}…  depth={depth}");
        Console.WriteLine();
    }

    // --- Write HTML artifact ---
    var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
    var outDir = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "results");
    Directory.CreateDirectory(outDir);
    var htmlPath = Path.Combine(outDir, $"fossil-analysis-{timestamp}.html");
    await File.WriteAllTextAsync(htmlPath, BuildFossilAnalysisHtml(analysis, timestamp));

    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"  HTML report: {htmlPath}");
    Console.ResetColor();
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
    Console.WriteLine($"  Retrieval samp:  {r.FossilRetrievalSamples}");
    var conversion = r.FossilRetrievalSamples > 0
        ? (double)r.FossilHits / r.FossilRetrievalSamples
        : 0d;
    Console.WriteLine($"  Fossil conv:     {conversion:P1} ({r.FossilHits}/{r.FossilRetrievalSamples})");
    Console.WriteLine($"  Struct rejects:  {r.StructuralGateRejections}");
    if (r.GraphPlannerUsed)
    {
        Console.WriteLine($"  Planner exp:     {r.GraphPlannerExpansions}");
        Console.WriteLine($"  Planner accept:  {r.GraphPlannerAcceptedTransitions}");
        Console.WriteLine($"  LLM fallback:    {(r.LegacyLlmFallbackUsed ? "yes" : "no")}");
    }
    Console.WriteLine($"  Avg fossil sim:  {(r.AvgFossilSimilarity > 0 ? r.AvgFossilSimilarity.ToString("F3") : "—")}");
    Console.WriteLine($"  LLM calls:       Qwen={r.LlmCallsTier1}  Flash={r.LlmCallsTier2}  Pro={r.LlmCallsTier3}");
    Console.WriteLine($"  Estimated cost:  ${r.EstimatedCostUsd:F4}");
    Console.WriteLine($"  Duration:        {r.TotalDuration.TotalMinutes:F1} min");
    r.Tier075Telemetry.LogMetrics(r.ProblemId);
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
    var totalRetrievalSamples = results.Sum(r => r.FossilRetrievalSamples);
    Console.WriteLine($"Retrieval samples:  {totalRetrievalSamples}");
    var totalConversion = totalRetrievalSamples > 0
        ? (double)results.Sum(r => r.FossilHits) / totalRetrievalSamples
        : 0d;
    Console.WriteLine($"Fossil conv rate:   {totalConversion:P1} ({results.Sum(r => r.FossilHits)}/{totalRetrievalSamples})");
    Console.WriteLine($"Struct rejects:     {results.Sum(r => r.StructuralGateRejections)}");
    var plannerRuns = results.Count(r => r.GraphPlannerUsed);
    Console.WriteLine($"Planner runs:       {plannerRuns}");
    Console.WriteLine($"Planner expansions: {results.Sum(r => r.GraphPlannerExpansions)}");
    Console.WriteLine($"Planner accepts:    {results.Sum(r => r.GraphPlannerAcceptedTransitions)}");
    Console.WriteLine($"Planner fallback:   {results.Count(r => r.LegacyLlmFallbackUsed)}");
    Console.WriteLine($"Total spend:        ${totalSpent:F2}");

    var telemetry = new GraphProposalTelemetry();
    foreach (var result in results)
        telemetry.MergeFrom(result.Tier075Telemetry);

    Console.WriteLine();
    telemetry.LogMetrics("benchmark-total");
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
              <td class="num">{(r.AvgFossilSimilarity > 0 ? r.AvgFossilSimilarity.ToString("F3") : "—")}</td>
              <td class="num">{(r.Outcome != ProofOutcome.Solved && r.BestMissedFossilSim > 0 ? r.BestMissedFossilSim.ToString("F3") + (r.BestMissedFossilSim >= 0.85f ? " ⚠️" : "") : "—")}</td>
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
                <th>Avg sim</th>
                <th>Near-miss</th>
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

/// <summary>Phase 8 fossil analysis HTML report.</summary>
static string BuildFossilAnalysisHtml(NexusAgent.Core.Models.FossilAnalysis a, string timestamp)
{
    var domainRows = new StringBuilder();
    foreach (var (domain, count) in a.DomainDistribution.OrderByDescending(kv => kv.Value))
        domainRows.Append($"<tr><td>{HtmlEsc(domain)}</td><td>{count}</td></tr>");

    var fossilRows = new StringBuilder();
    foreach (var f in a.TopFossils)
    {
        var cls = f.UseCount > 0 ? "universal" : "";
        fossilRows.Append(
            $"<tr class=\"{cls}\">" +
            $"<td><code>{HtmlEsc(f.Id[..Math.Min(12, f.Id.Length)])}…</code></td>" +
            $"<td>{HtmlEsc(f.DomainTag)}</td>" +
            $"<td><strong>{f.UseCount}</strong></td>" +
            $"<td>{f.SorryReduction}</td>" +
            $"<td class=\"snippet\">{HtmlEsc(Truncate(f.SubgoalSnippet.Replace('\n', ' '), 100))}</td>" +
            $"<td class=\"snippet\"><code>{HtmlEsc(Truncate(f.TacticSnippet.Replace('\n', ' '), 80))}</code></td>" +
            $"<td>{f.SourceProblems.Length}</td>" +
            $"</tr>");
    }

    var chainRows = new StringBuilder();
    foreach (var (rootId, depth) in a.DeepestPrecedesChains.OrderByDescending(kv => kv.Value))
        chainRows.Append($"<tr><td><code>{HtmlEsc(rootId[..Math.Min(12, rootId.Length)])}…</code></td><td>{depth}</td></tr>");

    return $$"""
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8"/>
          <title>NexusAgent — Phase 8 Fossil Analysis</title>
          <style>
            :root { --bg:#0d1117; --fg:#e6edf3; --accent:#58a6ff; --muted:#8b949e;
                    --card:#161b22; --border:#30363d; --green:#3fb950; --yellow:#d29922; }
            body  { background:var(--bg); color:var(--fg); font-family:system-ui,sans-serif;
                    max-width:1200px; margin:40px auto; padding:0 20px; }
            h1    { color:var(--accent); }
            .subtitle { color:var(--muted); margin-bottom:24px; }
            .cards { display:flex; gap:16px; flex-wrap:wrap; margin-bottom:32px; }
            .card { background:var(--card); border:1px solid var(--border); border-radius:8px;
                    padding:16px 24px; min-width:140px; }
            .card-value { font-size:2rem; font-weight:700; color:var(--accent); }
            .card-label { font-size:0.8rem; color:var(--muted); margin-top:4px; }
            h2 { margin-top:32px; border-bottom:1px solid var(--border); padding-bottom:8px; }
            table { width:100%; border-collapse:collapse; margin-top:12px; }
            th { text-align:left; padding:8px 12px; border-bottom:2px solid var(--border);
                 color:var(--muted); font-size:0.85rem; }
            td { padding:8px 12px; border-bottom:1px solid var(--border); font-size:0.85rem; }
            tr.universal td { background:rgba(63,185,80,0.07); }
            .snippet { max-width:340px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
            code { font-family:'JetBrains Mono','Fira Code',monospace; font-size:0.78rem; color:var(--accent); }
            .footer { margin-top:32px; color:var(--muted); font-size:0.8rem; }
          </style>
        </head>
        <body>
          <h1>NexusAgent — Phase 8 Fossil Vault Analysis</h1>
          <div class="subtitle">Run: {{timestamp}} · Rich Learning Nexus Challenge</div>

          <div class="cards">
            <div class="card"><div class="card-value">{{a.TotalFossils}}</div><div class="card-label">Proof fossils</div></div>
            <div class="card"><div class="card-value">{{a.TotalLandmarks}}</div><div class="card-label">Landmarks</div></div>
            <div class="card"><div class="card-value">{{a.SolvedProblems}}</div><div class="card-label">Problems solved</div></div>
            <div class="card"><div class="card-value">{{a.CrossRunHits}}</div><div class="card-label">Cross-run hits</div></div>
            <div class="card"><div class="card-value">{{a.DomainDistribution.Count}}</div><div class="card-label">Domains covered</div></div>
          </div>

          <h2>Domain Distribution</h2>
          <table>
            <thead><tr><th>Domain</th><th>Fossil count</th></tr></thead>
            <tbody>{{domainRows}}</tbody>
          </table>

          <h2>Top Fossils by Reuse — Universal Lemmas</h2>
          <p style="color:var(--muted);font-size:0.85rem;">
            Highlighted rows (green) are fossils reused across multiple problems —
            these are the <em>universal lemmas</em> of the proof domain.
            Higher reuse = more structural value; they form the backbone of the fossil-guided search.
          </p>
          <table>
            <thead>
              <tr>
                <th>Fossil ID</th><th>Domain</th><th>Uses</th><th>sorry↓</th>
                <th>Sub-goal snippet</th><th>Tactic snippet</th><th>Source problems</th>
              </tr>
            </thead>
            <tbody>{{fossilRows}}</tbody>
          </table>

          {{(a.DeepestPrecedesChains.Count > 0 ? $"""
          <h2>Deepest PRECEDES Chains (Tactic Backbone)</h2>
          <table>
            <thead><tr><th>Root fossil</th><th>Chain depth</th></tr></thead>
            <tbody>{chainRows}</tbody>
          </table>
          """ : "")}}

          <div class="footer">
            Generated by NexusAgent CLI · DeepMind Nexus Challenge · {{DateTime.Now:yyyy-MM-dd HH:mm}}
          </div>
        </body>
        </html>
        """;
}

/// <summary>Captures per-problem bench results with metadata for reporting.</summary>
record BenchRecord(string Id, string Domain, string Statement, ProofResult Result);
