using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NexusAgent.Core.Memory;

/// <summary>
/// Runs the ErdosHypergraph FC100 search in parallel by splitting the 100 goals
/// into N shards and spawning N <c>lake env lean ErdosHypergraph.lean</c> processes.
///
/// <para>Design decisions (from Opus 4.8 architecture review):</para>
/// <list type="bullet">
///   <item>Round-robin goal assignment (goal i → shard i % N) flattens tail latency.</item>
///   <item>Each shard runs 6 negative controls before emitting results; the aggregator
///       hard-gates on <c>{"type":"control","result":"ok"}</c> — shards that don't
///       self-certify are discarded entirely.</item>
///   <item>Results transported via stdout prefixed with <c>##NEXUS## </c> sentinel;
///       stray Lean/lake messages on the same stream are filtered out safely.</item>
///   <item>N is capped by CPU count and available RAM (heuristic: 1.5 GB/process).</item>
///   <item>:HgProofResult Neo4j persistence is deferred — aggregate in memory, print.</item>
/// </list>
/// </summary>
public sealed class HgParallelSolver
{
    private const string Sentinel = "##NEXUS## ";
    private const long   RamPerProcessBytes = 1_500L * 1024 * 1024; // 1.5 GB heuristic

    private readonly string _leanProjectPath;
    private readonly string _engineFile;
    private readonly ILogger<HgParallelSolver> _log;

    /// <param name="leanProjectPath">
    ///   Path to the formal-conjectures lake project (where <c>lakefile.toml</c> lives).
    /// </param>
    /// <param name="engineFile">
    ///   Absolute path to <c>ErdosHypergraph.lean</c>.
    /// </param>
    public HgParallelSolver(
        string leanProjectPath,
        string engineFile,
        ILogger<HgParallelSolver> log)
    {
        _leanProjectPath = leanProjectPath;
        _engineFile      = engineFile;
        _log             = log;
    }

    /// <summary>
    /// Run the FC100 search across <paramref name="shardCount"/> parallel Lean processes.
    /// Returns the aggregated list of per-goal results.
    /// </summary>
    public async Task<HgScanSummary> ScanAsync(
        IReadOnlyList<string> goalNames,
        int shardCount,
        TimeSpan processTimeout,
        bool holdout,
        CancellationToken ct)
    {
        var n = EffectiveShardCount(shardCount, goalNames.Count);
        _log.LogInformation("[HgParallelSolver] {Goals} goals → {N} shards (timeout {T}{H})",
            goalNames.Count, n, processTimeout, holdout ? ", HOLDOUT" : "");

        // Round-robin assignment: goal i → shard i % n  (pass indices, not names)
        // Indices avoid String.toName roundtrip issues for Arxiv.«»-style Lean names.
        var shardIndices = new List<int>[n];
        for (var i = 0; i < n; i++) shardIndices[i] = [];
        for (var i = 0; i < goalNames.Count; i++)
            shardIndices[i % n].Add(i);

        using var timeout = new CancellationTokenSource(processTimeout);
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        var tasks = shardIndices
            .Select((indices, idx) => RunShardAsync(idx, indices, goalNames, holdout, linked.Token))
            .ToList();

        var shardResults = await Task.WhenAll(tasks);

        return Aggregate(shardResults);
    }

    // ── Private helpers ─────────────────────────────────────────────────────

    private async Task<ShardResult> RunShardAsync(
        int shardIdx,
        List<int> goalIndices,
        IReadOnlyList<string> allGoalNames,
        bool holdout,
        CancellationToken ct)
    {
        if (goalIndices.Count == 0)
            return new ShardResult(shardIdx, ControlPassed: true, [], TimedOut: false);

        // Pass 0-based indices so the Lean engine looks up by position in fc100Decls.
        // This avoids String.toName roundtrip problems with Arxiv.«»-style names.
        var goalsEnv = string.Join(",", goalIndices);
        _log.LogDebug("[shard {I}] starting ({N} goals) indices: {G}",
            shardIdx, goalIndices.Count, goalsEnv[..Math.Min(80, goalsEnv.Length)]);

        var extraEnv = new Dictionary<string, string>
        {
            ["NEXUS_HG_SHARD_GOALS"] = goalsEnv,
        };
        if (holdout)
            extraEnv["NEXUS_HG_HOLDOUT"] = "1";

        var result = await Oracle.LeanProcessLauncher.RunAsync(
            _engineFile, _leanProjectPath, extraEnv, ct);

        if (result.TimedOut)
        {
            _log.LogWarning("[shard {I}] timed out after {T}", shardIdx, result.Elapsed);
            return new ShardResult(shardIdx, ControlPassed: false, [], TimedOut: true);
        }

        if (result.ExitCode != 0)
            _log.LogWarning("[shard {I}] exit code {Code}", shardIdx, result.ExitCode);

        return ParseShardOutput(shardIdx, result.Stdout, result.Stderr);
    }

    internal ShardResult ParseShardOutput(int shardIdx, string stdout, string stderr)
    {
        // Log stderr lines for debugging (Lean elaboration messages, etc.)
        foreach (var line in stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            _log.LogDebug("[shard {I} stderr] {L}", shardIdx, line);

        var nexusLines = stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith(Sentinel, StringComparison.Ordinal))
            .Select(l => l[Sentinel.Length..].Trim())
            .ToList();

        if (nexusLines.Count == 0)
        {
            _log.LogWarning("[shard {I}] no ##NEXUS## lines in stdout — discarding", shardIdx);
            return new ShardResult(shardIdx, ControlPassed: false, [], TimedOut: false);
        }

        // First sentineled line must be the control result
        var controlPassed = false;
        try
        {
            var first = JsonNode.Parse(nexusLines[0]);
            if (first?["type"]?.GetValue<string>() == "control" &&
                first["result"]?.GetValue<string>() == "ok")
                controlPassed = true;
        }
        catch { /* malformed — treated as FAIL */ }

        if (!controlPassed)
        {
            _log.LogError("[shard {I}] soundness gate FAILED — discarding all results", shardIdx);
            return new ShardResult(shardIdx, ControlPassed: false, [], TimedOut: false);
        }

        // Parse result lines
        var goalResults = new List<HgGoalResult>();
        foreach (var line in nexusLines.Skip(1))
        {
            try
            {
                var node = JsonNode.Parse(line);
                if (node?["type"]?.GetValue<string>() != "result") continue;

                var name   = node["name"]?.GetValue<string>() ?? "";
                var res    = node["result"]?.GetValue<string>() ?? "GAP";
                var steps  = node["steps"]?.AsArray()
                    .Select(s => s?["fn"]?.GetValue<string>() ?? "")
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList() ?? [];

                goalResults.Add(new HgGoalResult(name, res == "PROVED", steps));
            }
            catch { /* skip malformed line */ }
        }

        _log.LogInformation("[shard {I}] control=ok, {Proved} proved / {Total} total",
            shardIdx,
            goalResults.Count(r => r.Proved),
            goalResults.Count);

        return new ShardResult(shardIdx, ControlPassed: true, goalResults, TimedOut: false);
    }

    private HgScanSummary Aggregate(ShardResult[] shards)
    {
        var discarded = shards.Count(s => !s.ControlPassed);
        var allResults = shards
            .Where(s => s.ControlPassed)
            .SelectMany(s => s.GoalResults)
            .ToList();

        return new HgScanSummary(
            allResults,
            DiscardedShards: discarded,
            TotalShards: shards.Length);
    }

    private static int EffectiveShardCount(int requested, int goalCount)
    {
        var maxByCpu = Environment.ProcessorCount;
        // Heuristic RAM cap: don't spawn more Lean processes than RAM allows.
        // Each Lean + Mathlib process uses ~1.5 GB RSS.
        var freeMem = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var maxByRam = (int)Math.Max(1, freeMem / RamPerProcessBytes);

        var cap = Math.Min(maxByCpu, maxByRam);
        cap = Math.Min(cap, 8);                    // hard ceiling per Opus 4.8 review
        cap = Math.Min(cap, goalCount);             // never more shards than goals

        var effective = Math.Clamp(requested, 1, cap);
        return effective;
    }
}

// ── Result types ──────────────────────────────────────────────────────────────

/// <summary>One goal's proof-search result from a shard process.</summary>
public sealed record HgGoalResult(
    string       Name,
    bool         Proved,
    List<string> Steps)
{
    /// <summary>
    /// True when the only steps are the goal's own declaration name, internal
    /// proof terms of that declaration (<c>Name._proof_*</c>), or
    /// <c>assumption:*</c> discharges.  This means the engine "proved" the
    /// goal by applying the goal to itself — a look-up, not a composition.
    /// Such results are structurally valid Lean proofs only when the seed
    /// declaration is itself <c>sorry</c>-free; run the SorryAudit sweep to
    /// confirm.  They do NOT demonstrate the engine's generalisation ability.
    /// </summary>
    public bool IsSelfCitation =>
        Proved &&
        Steps.All(step =>
            step == Name ||
            step.StartsWith(Name + "._") ||
            step.StartsWith("assumption:"));
}

/// <summary>Raw output from one shard process.</summary>
internal sealed record ShardResult(
    int              ShardIdx,
    bool             ControlPassed,
    List<HgGoalResult> GoalResults,
    bool             TimedOut);

/// <summary>Aggregated result from all shards.</summary>
public sealed record HgScanSummary(
    List<HgGoalResult> Results,
    int                DiscardedShards,
    int                TotalShards)
{
    /// <summary>Goals proved by genuine external-lemma composition (not self-citation).</summary>
    public int GenuineCount  => Results.Count(r => r.Proved && !r.IsSelfCitation);
    /// <summary>Goals where the only proof step is the goal's own declaration (look-up, not composition).</summary>
    public int SelfCiteCount => Results.Count(r => r.IsSelfCitation);
    /// <summary>Total proved (genuine + self-cite). Use <see cref="GenuineCount"/> for substance.</summary>
    public int ProvedCount   => Results.Count(r => r.Proved);
    public int GapCount      => Results.Count(r => !r.Proved);
    public bool AnyDiscarded => DiscardedShards > 0;
}
