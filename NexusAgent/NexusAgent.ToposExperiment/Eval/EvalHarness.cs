using RichLearning.V2.Memory;
using RichLearning.V2.Models;
using NexusAgent.ToposExperiment.Models;
using NexusAgent.ToposExperiment.Search;
using NexusAgent.ToposExperiment.Telemetry;
using System.Diagnostics;

namespace NexusAgent.ToposExperiment.Eval;

/// <summary>
/// Eval harness (L9 + L10). Verbatim logic from <c>NexusAgent.RlbExperiment.Eval.EvalHarness</c>
/// — no type loosening needed (it consumes HyperEdge via IReadOnlyDictionary, same as
/// CreditAssignment). The L9 eval-freeze assert is the canary for the reference-stability
/// contract: if a backend returned fresh HyperEdge POCOs per read, the snapshot and the live
/// object would be different instances and this assert would fire (or, worse, theta mutation
/// would land on copies and silently no-op). ToposGraphMemory honors the contract by caching.
/// </summary>
internal sealed class EvalHarness
{
    private readonly NexusBackwardChainer _chainer;
    private readonly IReadOnlyDictionary<string, HyperEdge> _hyperedgeIndex;

    public EvalHarness(
        NexusBackwardChainer chainer,
        IReadOnlyDictionary<string, HyperEdge> hyperedgeIndex)
    {
        _chainer = chainer;
        _hyperedgeIndex = hyperedgeIndex;
    }

    public async Task<EvalSummary> RunAsync(
        List<GoalEntry> evalRoots,
        int fuel = 200,
        CancellationToken ct = default)
    {
        // L9: snapshot theta before eval.
        var thetaSnapshot = SnapshotTheta();

        var allTelemetry = new List<EpisodeTelemetry>();

        foreach (var arm in new[] { SearchArm.BaselineN, SearchArm.BaselineH, SearchArm.Learned })
        {
            var armName = arm.ToString();
            int solved = 0;

            foreach (var goal in evalRoots)
            {
                ct.ThrowIfCancellationRequested();

                var dag = new TrajectoryDag();
                var sw  = Stopwatch.StartNew();

                var result = await _chainer.SearchAsync(
                    goal.GoalHash, goal.StateVector, arm, fuel, dag, ct);

                sw.Stop();
                if (result.IsSuccess) solved++;

                int fired = dag.AllNodes.Count(n => n.FiredHyperedgeId is not null);

                allTelemetry.Add(new EpisodeTelemetry(
                    EpisodeId:            Guid.NewGuid().ToString("N")[..12],
                    TheoremId:            goal.TheoremId,
                    GoalHash:             goal.GoalHash,
                    Arm:                  armName,
                    Solved:               result.IsSuccess,
                    Steps:                result.Steps,
                    ThetaEdgesReinforced: 0, // eval is read-only
                    MeanThetaMagnitude:   0.0,
                    HyperedgesFired:      fired,
                    NegativeReinforced:   0,
                    ElapsedMs:            sw.Elapsed.TotalMilliseconds));
            }

            Console.WriteLine($"[EvalHarness] arm={armName} solved={solved}/{evalRoots.Count} " +
                              $"({100.0 * solved / Math.Max(1, evalRoots.Count):F1}%)");
        }

        // L9: assert theta unchanged after eval.
        AssertThetaUnchanged(thetaSnapshot);

        // L10: theta-coverage fraction.
        int totalEdges    = _hyperedgeIndex.Count;
        int trainedEdges  = _hyperedgeIndex.Values.Count(e => e.ThetaParameters is not null);
        double coverage   = totalEdges > 0 ? (double)trainedEdges / totalEdges : 0.0;

        return new EvalSummary(allTelemetry, coverage, totalEdges, trainedEdges);
    }

    // ── L9 theta snapshot ────────────────────────────────────────────────────

    private Dictionary<string, double[]> SnapshotTheta()
    {
        var snap = new Dictionary<string, double[]>(StringComparer.Ordinal);
        foreach (var (id, edge) in _hyperedgeIndex)
        {
            if (edge.ThetaParameters is { } tp)
                snap[id] = (double[])tp.Theta.Clone();
        }
        return snap;
    }

    private void AssertThetaUnchanged(Dictionary<string, double[]> snapshot)
    {
        foreach (var (id, original) in snapshot)
        {
            if (!_hyperedgeIndex.TryGetValue(id, out var edge))
                throw new InvalidOperationException(
                    $"L9 eval-freeze violation: HyperEdge '{id}' disappeared during eval.");

            if (edge.ThetaParameters is null)
                throw new InvalidOperationException(
                    $"L9 eval-freeze violation: HyperEdge '{id}' lost its ThetaParameters during eval.");

            var current = edge.ThetaParameters.Theta;
            if (current.Length != original.Length)
                throw new InvalidOperationException(
                    $"L9 eval-freeze violation: HyperEdge '{id}' theta length changed: " +
                    $"{original.Length} → {current.Length}");

            for (int i = 0; i < original.Length; i++)
            {
                if (current[i] != original[i]) // bit-identical check
                    throw new InvalidOperationException(
                        $"L9 eval-freeze violation: HyperEdge '{id}' theta[{i}] mutated during eval: " +
                        $"{original[i]:G17} → {current[i]:G17}");
            }
        }
    }
}

public sealed record EvalSummary(
    List<EpisodeTelemetry> Telemetry,
    double ThetaCoverage,
    int TotalHyperEdges,
    int TrainedHyperEdges);
