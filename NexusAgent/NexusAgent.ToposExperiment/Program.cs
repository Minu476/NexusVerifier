using NexusAgent.Core.Configuration;
using NexusAgent.ToposExperiment.Fixtures;
using NexusAgent.ToposExperiment.Eval;
using NexusAgent.ToposExperiment.Ingest;
using NexusAgent.ToposExperiment.Memory;
using NexusAgent.ToposExperiment.Models;
using NexusAgent.ToposExperiment.NarySearch;
using NexusAgent.ToposExperiment.Search;
using NexusAgent.ToposExperiment.Telemetry;
using NexusAgent.ToposExperiment.Training;
using RichLearning.V2.Abstractions;
using RichLearning.V2.Models;
using System.Text.Json;

// ── Parse CLI arguments ───────────────────────────────────────────────────────
int seed         = GetInt("--seed",     42);
int fuel         = GetInt("--fuel",     200);
int episodes     = GetInt("--episodes", 500);
bool runTraining = GetFlag("--run-training");
bool runEval     = GetFlag("--run-eval");
bool runAll      = !runTraining && !runEval; // default: run both

// --fixture: "synthetic" (default, zero-dependency, real junctions) or "neo4j" (the live nexusdb
// graph — requires the shared Desktop instance, see docs/GDS_ORACLE_SETUP.md §4.2 credential note).
string fixture   = GetString("--fixture", "synthetic");

// --chain: "nary" (default — Topos-native chainer, no V2 projection) or "projected" (the
// faithful-port path: stores n-ary in Topos but projects to V2 HyperEdge at the boundary).
// "nary" is the deferred rewrite the faithful-port milestone left for later — the chainer reads
// the genuine n-ary shape directly, and credit assignment reinforces a Topos LearnableEdge
// property through the kernel (no HyperEdge, no reference-stability contract). See
// docs/TOPOS_INTEGRATION_REPORT.md §"n-ary chainer rewrite".
string chain     = GetString("--chain", "nary");

if (runAll) { runTraining = true; runEval = true; }

Console.WriteLine($"NexusAgent.ToposExperiment  fixture={fixture}  chain={chain}");
Console.WriteLine($"  seed={seed}  fuel={fuel}  episodes={episodes}  training={runTraining}  eval={runEval}");

// ── Load the graph (synthetic fixture or live Neo4j) ─────────────────────────
List<AppliesEdge> allEdges;
Dictionary<string, float[]> vectors;

if (string.Equals(fixture, "synthetic", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("[Program] loading synthetic APPLIES fixture (real junctions, no Neo4j)…");
    (allEdges, vectors) = SyntheticAppliesGraph.Build();
}
else
{
    var cfg = new NexusConfig();
    cfg.ApplyEnvironmentOverrides();
    await using var loader = new RlbGraphLoader(cfg);
    (allEdges, vectors) = await loader.LoadGraphAsync();
}

Console.WriteLine($"[Program] {allEdges.Count:N0} APPLIES edges, {vectors.Count:N0} vectors");

// ── Compute root goals (shared across both chain paths) ──────────────────────
var (trainRoots, evalRoots) = RlbGraphLoader.ComputeRootGoals(allEdges, vectors);
if (trainRoots.Count == 0 && evalRoots.Count == 0)
{
    Console.WriteLine("[Program] WARNING: zero root goals derived — synthetic fixture may need richer shape, " +
                      "or Neo4j load returned no edges. Eval will be empty.");
}

// ── Dispatch on chain path ───────────────────────────────────────────────────
if (string.Equals(chain, "nary", StringComparison.OrdinalIgnoreCase))
    await RunNaryChainAsync(allEdges, vectors, trainRoots, evalRoots, seed, fuel, episodes, runTraining, runEval);
else if (string.Equals(chain, "projected", StringComparison.OrdinalIgnoreCase))
    await RunProjectedChainAsync(allEdges, vectors, trainRoots, evalRoots, seed, fuel, episodes, runTraining, runEval);
else
    throw new ArgumentException($"--chain must be 'nary' or 'projected', got '{chain}'.");

Console.WriteLine("\nDone.");

// ──────────────────────────────────────────────────────────────────────────────
// N-ARY CHAIN PATH — Topos-native chainer, no V2 projection.
// ──────────────────────────────────────────────────────────────────────────────
async Task RunNaryChainAsync(
    List<AppliesEdge> allEdges, Dictionary<string, float[]> vectors,
    List<GoalEntry> trainRoots, List<GoalEntry> evalRoots,
    int seed, int fuel, int episodes, bool runTraining, bool runEval)
{
    Console.WriteLine("[Program] building Topos-native kernel (no V2 projection)…");
    var build = await ToposAppliesAdapter.BuildNaryAsync(allEdges);

    var chainer = new NaryBackwardChainer(build.Kernel, build.GoalHandleByHash, vectors);

    // ── Training ─────────────────────────────────────────────────────────────
    List<EpisodeTelemetry> trainTelemetry = [];
    if (runTraining && trainRoots.Count > 0)
    {
        Console.WriteLine($"\n=== TRAINING  (n-ary chain)  episodes={episodes}  seed={seed}  fuel={fuel} ===");
        var trainingLoop = new NaryTrainingLoop(chainer, build.Kernel);
        trainTelemetry = await trainingLoop.RunAsync(trainRoots, episodes, seed, fuel);

        int trainSolved = trainTelemetry.Count(t => t.Solved);
        int thetaTotal  = trainTelemetry.Sum(t => t.ThetaEdgesReinforced);
        Console.WriteLine($"[Training] episodes={trainTelemetry.Count}  solved={trainSolved}  " +
                          $"theta_edges_reinforced={thetaTotal}");

        WriteTelemetryCsv("train_telemetry_nary.csv", trainTelemetry);
    }

    // ── Eval ─────────────────────────────────────────────────────────────────
    if (runEval && evalRoots.Count > 0)
    {
        Console.WriteLine($"\n=== EVAL  (n-ary chain)  goals={evalRoots.Count}  fuel={fuel} ===");
        var harness = new NaryEvalHarness(chainer, build.Kernel);
        var summary = await harness.RunAsync(evalRoots, fuel);

        Console.WriteLine($"\n[Eval] theta_coverage={summary.ThetaCoverage:P2} " +
                          $"({summary.TrainedHyperEdges:N0}/{summary.TotalHyperEdges:N0} edges trained)");

        WriteTelemetryCsv("eval_telemetry_nary.csv", summary.Telemetry);
        WriteNaryEvalSummaryJson("eval_summary_nary.json", summary, seed, fuel, episodes, fixture);
    }
}

// ──────────────────────────────────────────────────────────────────────────────
// PROJECTED CHAIN PATH — the faithful-port baseline (Topos storage, V2 boundary).
// Kept so the two paths can be compared apples-to-apples on the same data.
// ──────────────────────────────────────────────────────────────────────────────
async Task RunProjectedChainAsync(
    List<AppliesEdge> allEdges, Dictionary<string, float[]> vectors,
    List<GoalEntry> trainRoots, List<GoalEntry> evalRoots,
    int seed, int fuel, int episodes, bool runTraining, bool runEval)
{
    Console.WriteLine("[Program] building Topos backend with V2 projection…");
    IGraphMemory memory = await ToposAppliesAdapter.BuildMemoryAsync(allEdges);

    var hyperedgeIndex = await BuildProjectedHyperedgeIndexAsync(memory, allEdges);
    Console.WriteLine($"[Program] hyperedge index: {hyperedgeIndex.Count:N0} entries");

    var chainer = new NexusBackwardChainer(memory, vectors);

    // ── Training ─────────────────────────────────────────────────────────────
    List<EpisodeTelemetry> trainTelemetry = [];
    if (runTraining && trainRoots.Count > 0)
    {
        Console.WriteLine($"\n=== TRAINING  (projected chain)  episodes={episodes}  seed={seed}  fuel={fuel} ===");
        var trainingLoop = new TrainingLoop(memory, chainer, hyperedgeIndex);
        trainTelemetry = await trainingLoop.RunAsync(trainRoots, episodes, seed, fuel);

        int trainSolved = trainTelemetry.Count(t => t.Solved);
        int thetaTotal  = trainTelemetry.Sum(t => t.ThetaEdgesReinforced);
        Console.WriteLine($"[Training] episodes={trainTelemetry.Count}  solved={trainSolved}  " +
                          $"theta_edges_reinforced={thetaTotal}");

        WriteTelemetryCsv("train_telemetry_projected.csv", trainTelemetry);
    }

    // ── Eval ─────────────────────────────────────────────────────────────────
    if (runEval && evalRoots.Count > 0)
    {
        Console.WriteLine($"\n=== EVAL  (projected chain)  goals={evalRoots.Count}  fuel={fuel} ===");
        var harness = new EvalHarness(chainer, hyperedgeIndex);
        var summary = await harness.RunAsync(evalRoots, fuel);

        Console.WriteLine($"\n[Eval] theta_coverage={summary.ThetaCoverage:P2} " +
                          $"({summary.TrainedHyperEdges:N0}/{summary.TotalHyperEdges:N0} edges trained)");

        WriteTelemetryCsv("eval_telemetry_projected.csv", summary.Telemetry);
        WriteProjectedEvalSummaryJson("eval_summary_projected.json", summary, seed, fuel, episodes, fixture);
    }
}

// ── Helpers ───────────────────────────────────────────────────────────────────

static int GetInt(string flag, int defaultValue)
{
    var args = Environment.GetCommandLineArgs();
    int idx  = Array.IndexOf(args, flag);
    return idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], out var v) ? v : defaultValue;
}

static bool GetFlag(string flag) =>
    Environment.GetCommandLineArgs().Contains(flag, StringComparer.OrdinalIgnoreCase);

static string GetString(string flag, string defaultValue)
{
    var args = Environment.GetCommandLineArgs();
    int idx  = Array.IndexOf(args, flag);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : defaultValue;
}

static async Task<Dictionary<string, HyperEdge>> BuildProjectedHyperedgeIndexAsync(
    IGraphMemory memory, List<AppliesEdge> edges)
{
    var index = new Dictionary<string, HyperEdge>(StringComparer.Ordinal);
    var anchors = edges.Select(e => e.GoalBeforeHash).Distinct(StringComparer.Ordinal).ToList();
    foreach (var anchor in anchors)
    {
        var candidates = await memory.GetHyperedgesByMemberAsync(
            new StateKey(anchor), HyperEdgeRole.Anchor);
        foreach (var he in candidates)
            index.TryAdd(he.Id.StableIdentity, he);
    }
    return index;
}

static void WriteTelemetryCsv(string path, List<EpisodeTelemetry> rows)
{
    using var w = new StreamWriter(path);
    w.WriteLine("episodeId,theoremId,goalHash,arm,solved,steps,thetaEdges,meanThetaMag,hyperedgesFired,negReinforced,elapsedMs");
    foreach (var r in rows)
        w.WriteLine($"{r.EpisodeId},{r.TheoremId},{r.GoalHash},{r.Arm},{r.Solved}," +
                    $"{r.Steps},{r.ThetaEdgesReinforced},{r.MeanThetaMagnitude:F6}," +
                    $"{r.HyperedgesFired},{r.NegativeReinforced},{r.ElapsedMs:F1}");
    Console.WriteLine($"[Output] {path}  ({rows.Count:N0} rows)");
}

static void WriteProjectedEvalSummaryJson(string path, EvalSummary summary, int seed, int fuel, int episodes, string fixture)
{
    var armStats = summary.Telemetry
        .GroupBy(t => t.Arm)
        .Select(g => new
        {
            arm        = g.Key,
            goals      = g.Count(),
            solved     = g.Count(t => t.Solved),
            solveRate  = g.Count(t => t.Solved) / (double)Math.Max(1, g.Count()),
            meanSteps  = g.Where(t => t.Solved).Select(t => (double)t.Steps).DefaultIfEmpty(0).Average(),
        })
        .ToList();

    var doc = new
    {
        backend = "topos", chain = "projected", fixture, seed, fuel, episodes,
        thetaCoverage     = summary.ThetaCoverage,
        totalHyperEdges   = summary.TotalHyperEdges,
        trainedHyperEdges = summary.TrainedHyperEdges,
        armStats,
    };

    File.WriteAllText(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"[Output] {path}");
}

static void WriteNaryEvalSummaryJson(string path, NaryEvalSummary summary, int seed, int fuel, int episodes, string fixture)
{
    var armStats = summary.Telemetry
        .GroupBy(t => t.Arm)
        .Select(g => new
        {
            arm        = g.Key,
            goals      = g.Count(),
            solved     = g.Count(t => t.Solved),
            solveRate  = g.Count(t => t.Solved) / (double)Math.Max(1, g.Count()),
            meanSteps  = g.Where(t => t.Solved).Select(t => (double)t.Steps).DefaultIfEmpty(0).Average(),
        })
        .ToList();

    var doc = new
    {
        backend = "topos", chain = "nary", fixture, seed, fuel, episodes,
        thetaCoverage     = summary.ThetaCoverage,
        totalHyperEdges   = summary.TotalHyperEdges,
        trainedHyperEdges = summary.TrainedHyperEdges,
        armStats,
    };

    File.WriteAllText(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"[Output] {path}");
}
