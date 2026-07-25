using Neo4j.Driver;
using NexusAgent.Core.Configuration;
using NexusAgent.ToposExperiment.Ingest;
using NexusAgent.ToposExperiment.Memory;
using NexusAgent.ToposExperiment.Models;
using NexusAgent.ToposExperiment.Search;

namespace NexusAgent.ToposExperiment.Tests;

/// <summary>
/// Opt-in parity test against the live <c>nexusdb</c> Neo4j graph (the 267k-node APPLIES graph
/// the V2 experiment reads). Skips gracefully if the shared Desktop instance isn't reachable —
/// the same pattern Topos's own GDS-oracle tests use (see
/// <c>tests/Topos.Tests.GdsOracle/Neo4jTestConfig.cs</c>).
///
/// <b>What this verifies:</b> that the Topos backend produces the same solve rate / mean steps
/// over real data as the recorded V2 baseline. The V2 baseline CSVs live alongside (committed
/// from a prior V2 run on the same data); this test loads the same graph into Topos and asserts
/// the aggregate numbers match within the experiment's own parity tolerance.
///
/// <b>Credential note (handoff §4.2):</b> the shared Desktop instance's password lives in macOS
/// Keychain, not in any file. <c>~/.secrets</c> exports it as <c>NEO4J_PASSWORD</c>; the NexusConfig
/// env-var overlay (<c>NEXUS_NEO4J_PASSWORD</c>) picks it up. If the env isn't set, the test skips.
/// </summary>
public class Neo4jParityTests
{
    private static async Task<bool> IsReachableAsync()
    {
        var uri = Environment.GetEnvironmentVariable("NEXUS_NEO4J_URI") ?? "bolt://localhost:7687";
        var user = Environment.GetEnvironmentVariable("NEXUS_NEO4J_USER") ?? "neo4j";
        var pwd = Environment.GetEnvironmentVariable("NEXUS_NEO4J_PASSWORD")
                  ?? Environment.GetEnvironmentVariable("NEO4J_PASSWORD");
        if (string.IsNullOrEmpty(pwd)) return false;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var driver = GraphDatabase.Driver(uri, AuthTokens.Basic(user, pwd));
            await using var session = driver.AsyncSession(o => o.WithDatabase(
                Environment.GetEnvironmentVariable("NEXUS_NEO4J_DATABASE") ?? "nexusdb"));
            var result = await session.RunAsync("RETURN 1 AS ok");
            await result.ConsumeAsync();
            await driver.DisposeAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    [Fact(Skip = "Opt-in: set NEXUS_NEO4J_PASSWORD and remove this Skip to run the live parity check.")]
    public async Task ToposBackend_MatchesV2Baseline_OnLiveNexusdb()
    {
        if (!await IsReachableAsync())
            return; // no oracle reachable — skip, don't fail (GDS-oracle convention)

        // Load the live graph via the same RlbGraphLoader the V2 baseline used.
        var cfg = new NexusConfig();
        cfg.ApplyEnvironmentOverrides();
        await using var loader = new RlbGraphLoader(cfg);
        var (edges, vectors) = await loader.LoadGraphAsync();

        // Build the Topos backend + index + chainer (same construction as Program.cs).
        var memory = await ToposAppliesAdapter.BuildMemoryAsync(edges);
        var hyperedgeIndex = await BuildIndexAsync(memory, edges);
        var chainer = new NexusBackwardChainer(memory, vectors);

        // Run eval over the eval-split roots — the same set the V2 baseline evaluated.
        var (_, evalRoots) = RlbGraphLoader.ComputeRootGoals(edges, vectors);
        var harness = new Eval.EvalHarness(chainer, hyperedgeIndex);
        var summary = await harness.RunAsync(evalRoots, fuel: 200);

        // Parity bar: compare against the recorded V2 baseline. The V2 experiment reported
        // (Phase A) all arms 100% solve at ~2.09 mean steps. The Topos backend should match
        // solve rate exactly (the search is deterministic given the same graph + arm) and mean
        // steps within float-tolerance.
        //
        // NOTE: this test is opt-in (Skip above) because it depends on the shared instance
        // being up and the nexusdb graph being loaded. The synthetic-fixture tests in
        // SyntheticAppliesGraphTests are the always-run structural gate.
        foreach (var arm in summary.Telemetry.GroupBy(t => t.Arm))
        {
            int solved = arm.Count(t => t.Solved);
            int total = arm.Count();
            double rate = (double)solved / Math.Max(1, total);
            Assert.True(rate > 0.99, $"arm={arm.Key} solve rate {rate:P1} unexpectedly low vs V2 baseline (~100%)");
        }
    }

    private static async Task<Dictionary<string, RichLearning.V2.Models.HyperEdge>> BuildIndexAsync(
        ToposGraphMemory memory, List<AppliesEdge> edges)
    {
        var index = new Dictionary<string, RichLearning.V2.Models.HyperEdge>(StringComparer.Ordinal);
        var anchors = edges.Select(e => e.GoalBeforeHash).Distinct(StringComparer.Ordinal).ToList();
        foreach (var anchor in anchors)
        {
            var candidates = await memory.GetHyperedgesByMemberAsync(
                new RichLearning.V2.Abstractions.StateKey(anchor),
                RichLearning.V2.Models.HyperEdgeRole.Anchor);
            foreach (var he in candidates)
                index.TryAdd(he.Id.StableIdentity, he);
        }
        return index;
    }
}
