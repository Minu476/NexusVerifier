using NexusAgent.ToposExperiment.Models;
using NexusAgent.ToposExperiment.Telemetry;
using RichLearning.V2.Memory;
using System.Diagnostics;
using Topos.Hypergraph;

namespace NexusAgent.ToposExperiment.NarySearch;

/// <summary>
/// Eval harness for the n-ary chainer. Mirrors <see cref="Eval.EvalHarness"/>'s shape (three arms,
/// L9 eval-freeze assert, theta-coverage reporting) but operates over the Topos kernel directly.
///
/// <b>The L9 eval-freeze canary, n-ary version.</b> The projected path snapshotted
/// <c>HyperEdge.ThetaParameters.Theta</c> per edge and asserted bit-identical unchanged after
/// eval — proving eval was read-only. The n-ary equivalent snapshots every edge-vertex's
/// <c>LearnableEdge</c> theta (read fresh from the kernel) and asserts the same: eval must not
/// reinforce. Because <c>LearnableEdge</c> is immutable and the kernel is the single source of
/// truth, this check is structurally simpler than the projected path's — there's no
/// reference-identity question, just "did the kernel's stored value change between snapshot and
/// re-read?"
/// </summary>
internal sealed class NaryEvalHarness
{
    private readonly NaryBackwardChainer _chainer;
    private readonly HypergraphKernel _kernel;

    public NaryEvalHarness(NaryBackwardChainer chainer, HypergraphKernel kernel)
    {
        _chainer = chainer;
        _kernel = kernel;
    }

    public async Task<NaryEvalSummary> RunAsync(
        List<GoalEntry> evalRoots,
        int fuel = 200,
        CancellationToken ct = default)
    {
        var learnableProp = _kernel.ResolveProperty<LearnableEdge>("learnable");

        // L9: snapshot the kernel's LearnableEdge theta on every edge-vertex before eval.
        var thetaSnapshot = SnapshotTheta(learnableProp);

        var allTelemetry = new List<EpisodeTelemetry>();

        foreach (var arm in new[] { NarySearchArm.BaselineN, NarySearchArm.BaselineH, NarySearchArm.Learned })
        {
            var armName = arm.ToString();
            int solved = 0;

            foreach (var goal in evalRoots)
            {
                ct.ThrowIfCancellationRequested();

                var dag = new TrajectoryDag();
                var sw = Stopwatch.StartNew();

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
                    ThetaEdgesReinforced: 0,
                    MeanThetaMagnitude:   0.0,
                    HyperedgesFired:      fired,
                    NegativeReinforced:   0,
                    ElapsedMs:            sw.Elapsed.TotalMilliseconds));
            }

            Console.WriteLine($"[NaryEvalHarness] arm={armName} solved={solved}/{evalRoots.Count} " +
                              $"({100.0 * solved / Math.Max(1, evalRoots.Count):F1}%)");
        }

        // L9: assert theta unchanged after eval.
        AssertThetaUnchanged(learnableProp, thetaSnapshot);

        // Theta-coverage: fraction of edge-vertices with a non-default LearnableEdge.
        var edgeVertices = _kernel.VertexHandles()
            .Where(h => _kernel.TryGetVertex(h, out var v) && v.Roles == VertexRoles.Edge)
            .ToList();
        int totalEdges = edgeVertices.Count;
        int trainedEdges = edgeVertices.Count(h => _kernel.TryGetProperty(learnableProp, h, out _));
        double coverage = totalEdges > 0 ? (double)trainedEdges / totalEdges : 0.0;

        return new NaryEvalSummary(allTelemetry, coverage, totalEdges, trainedEdges);
    }

    // ── L9 theta snapshot over the kernel ────────────────────────────────────

    private Dictionary<uint, float[]> SnapshotTheta(PropertyKey<LearnableEdge> learnableProp)
    {
        var snap = new Dictionary<uint, float[]>();
        foreach (var h in _kernel.VertexHandles())
        {
            if (!_kernel.TryGetVertex(h, out var v) || v.Roles != VertexRoles.Edge) continue;
            if (_kernel.TryGetProperty(learnableProp, h, out var le))
                snap[h.Index] = (float[])le.Theta.Clone();
        }
        return snap;
    }

    private void AssertThetaUnchanged(PropertyKey<LearnableEdge> learnableProp, Dictionary<uint, float[]> snapshot)
    {
        foreach (var (idx, original) in snapshot)
        {
            // Re-resolve the handle — Index is stable (Handles are monotonic-never-reused per
            // Invariant 1), so the same Index maps to the same vertex across reads.
            var handle = new Handle(idx);
            if (!_kernel.TryGetProperty(learnableProp, handle, out var current))
                throw new InvalidOperationException(
                    $"L9 n-ary eval-freeze violation: edge-vertex #{idx} lost its LearnableEdge during eval.");

            if (current.Theta.Length != original.Length)
                throw new InvalidOperationException(
                    $"L9 n-ary eval-freeze violation: edge-vertex #{idx} theta length changed: " +
                    $"{original.Length} → {current.Theta.Length}");

            for (int i = 0; i < original.Length; i++)
            {
                if (current.Theta[i] != original[i])
                    throw new InvalidOperationException(
                        $"L9 n-ary eval-freeze violation: edge-vertex #{idx} theta[{i}] mutated during eval: " +
                        $"{original[i]:G17} → {current.Theta[i]:G17}");
            }
        }
    }
}

public sealed record NaryEvalSummary(
    List<EpisodeTelemetry> Telemetry,
    double ThetaCoverage,
    int TotalHyperEdges,
    int TrainedHyperEdges);
