using NexusAgent.Core.Configuration;
using NexusAgent.ToposExperiment.Fixtures;
using NexusAgent.ToposExperiment.Eval;
using NexusAgent.ToposExperiment.Ingest;
using NexusAgent.ToposExperiment.Memory;
using NexusAgent.ToposExperiment.Models;
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

if (runAll) { runTraining = true; runEval = true; }

Console.WriteLine($"NexusAgent.ToposExperiment  fixture={fixture}  (Topos backend)");
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

// ── Build the Topos backend ──────────────────────────────────────────────────
// This project tests Topos only. The V2 baseline comparison is done by running the separate
// NexusAgent.RlbExperiment project (on the rlb-v2-experiment branch) over the same data — both
// harnesses write identically-shaped telemetry CSVs / eval_summary.json for diffing. Keeping the
// V2 path out of this project avoids coupling ToposExperiment to the paused-experiment branch.
IGraphMemory memory = await ToposAppliesAdapter.BuildMemoryAsync(allEdges);

// Build the flat hyperedge index keyed by Id.StableIdentity — same construction as the V2
// Program.cs. Stores the live references the memory returned; CreditAssignment mutates these.
var hyperedgeIndex = await BuildHyperedgeIndexAsync(memory, allEdges);
Console.WriteLine($"[Program] hyperedge index: {hyperedgeIndex.Count:N0} entries");

// ── Compute root goals ────────────────────────────────────────────────────────
var (trainRoots, evalRoots) = RlbGraphLoader.ComputeRootGoals(allEdges, vectors);
if (trainRoots.Count == 0 && evalRoots.Count == 0)
{
    Console.WriteLine("[Program] WARNING: zero root goals derived — synthetic fixture may need richer shape, " +
                      "or Neo4j load returned no edges. Eval will be empty.");
}

var chainer = new NexusBackwardChainer(memory, vectors);

// ── Training ─────────────────────────────────────────────────────────────────
List<EpisodeTelemetry> trainTelemetry = [];
if (runTraining && trainRoots.Count > 0)
{
    Console.WriteLine($"\n=== TRAINING  (Topos backend)  episodes={episodes}  seed={seed}  fuel={fuel} ===");
    var trainingLoop = new TrainingLoop(memory, chainer, hyperedgeIndex);
    trainTelemetry = await trainingLoop.RunAsync(trainRoots, episodes, seed, fuel);

    int trainSolved = trainTelemetry.Count(t => t.Solved);
    int thetaTotal  = trainTelemetry.Sum(t => t.ThetaEdgesReinforced);
    Console.WriteLine($"[Training] episodes={trainTelemetry.Count}  solved={trainSolved}  " +
                      $"theta_edges_reinforced={thetaTotal}");

    WriteTelemetryCsv($"train_telemetry_topos.csv", trainTelemetry);
}

// ── Eval ─────────────────────────────────────────────────────────────────────
if (runEval && evalRoots.Count > 0)
{
    Console.WriteLine($"\n=== EVAL  (Topos backend)  goals={evalRoots.Count}  fuel={fuel} ===");
    var harness = new EvalHarness(chainer, hyperedgeIndex);
    var evalSummary = await harness.RunAsync(evalRoots, fuel);

    Console.WriteLine($"\n[Eval] theta_coverage={evalSummary.ThetaCoverage:P2} " +
                      $"({evalSummary.TrainedHyperEdges:N0}/{evalSummary.TotalHyperEdges:N0} edges trained)");

    WriteTelemetryCsv($"eval_telemetry_topos.csv", evalSummary.Telemetry);
    WriteEvalSummaryJson($"eval_summary_topos.json", evalSummary, seed, fuel, episodes, fixture);
}

Console.WriteLine("\nDone.");

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

static async Task<Dictionary<string, HyperEdge>> BuildHyperedgeIndexAsync(
    IGraphMemory memory,
    List<AppliesEdge> edges)
{
    var index = new Dictionary<string, HyperEdge>(StringComparer.Ordinal);

    // Derive all unique anchor keys from the loaded edges, then fetch their hyperedges.
    var anchors = edges
        .Select(e => e.GoalBeforeHash)
        .Distinct(StringComparer.Ordinal)
        .ToList();

    foreach (var anchor in anchors)
    {
        var candidates = await memory.GetHyperedgesByMemberAsync(
            new StateKey(anchor),
            HyperEdgeRole.Anchor);

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

static void WriteEvalSummaryJson(string path, EvalSummary summary, int seed, int fuel, int episodes, string fixture)
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
        backend = "topos",
        fixture,
        seed,
        fuel,
        episodes,
        thetaCoverage       = summary.ThetaCoverage,
        totalHyperEdges     = summary.TotalHyperEdges,
        trainedHyperEdges   = summary.TrainedHyperEdges,
        armStats,
    };

    File.WriteAllText(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"[Output] {path}");
}
