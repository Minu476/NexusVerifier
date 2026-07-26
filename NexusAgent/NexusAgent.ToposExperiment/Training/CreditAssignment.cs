using RichLearning.V2.Abstractions;
using RichLearning.V2.Memory;
using RichLearning.V2.Models;

namespace NexusAgent.ToposExperiment.Training;

/// <summary>
/// Post-episode credit assignment for HyperEdge theta parameters. Verbatim from
/// <c>NexusAgent.RlbExperiment.Training.CreditAssignment</c> — no type loosening needed (it
/// consumes HyperEdge via <c>IReadOnlyDictionary&lt;string, HyperEdge&gt;</c>, which is already
/// interface-shaped on the boundary). The reference-stability contract this class relies on is
/// the load-bearing one: <c>edge.ReinforceTheta</c> mutates the *same instance* the memory holds.
/// </summary>
internal static class CreditAssignment
{
    // ── DapsaKernel-matched constants ────────────────────────────────────────
    public const double DiscountFactor           = 0.95;
    public const double EdgeLearningRate         = 0.05;
    public const double FossilizationQThreshold  = 0.5;
    public const double ThetaCreditFloor         = 0.01;
    public const double NegativeRateMultiplier   = 2.0;
    public const double NegativeStabilityDamping = 0.5;

    /// <summary>
    /// Apply credit to each fired HyperEdge following the library algorithm exactly.
    /// Returns (thetaCount, meanMag, negCount, fired).
    /// </summary>
    public static (int thetaCount, double meanMag, int negCount, int fired) ReinforceFromTrajectory(
        TrajectoryDag dag,
        IReadOnlyDictionary<string, HyperEdge> hyperedgeIndex,
        double terminalReward)
    {
        int thetaCount = 0, negCount = 0;
        double magSum = 0.0;

        int fired = dag.AllNodes.Count(n => n.FiredHyperedgeId.HasValue);

        // ── Positive path (M4): high-value nodes only ────────────────────────
        var highValueNodes = dag.GetHighValueNodes(FossilizationQThreshold);
        if (highValueNodes.Count > 0)
        {
            var causalDistances = dag.GetCausalDistances();

            var decayedRewards = new Dictionary<PatternKey, double>(highValueNodes.Count);
            foreach (var node in highValueNodes)
            {
                var pk = new PatternKey(node.State, node.Action);
                causalDistances.TryGetValue(pk, out int d);
                double decayed = terminalReward * Math.Pow(DiscountFactor, d);
                if (!decayedRewards.TryGetValue(pk, out double existing) || decayed > existing)
                    decayedRewards[pk] = decayed;
            }

            foreach (var node in highValueNodes)
            {
                if (node.FiredHyperedgeId is not { } hyperEdgeId) continue;
                if (!TryGetContext(node.Metadata, out var ctx)) continue;

                var pk = new PatternKey(node.State, node.Action);
                if (!decayedRewards.TryGetValue(pk, out double r)) continue;
                if (!hyperedgeIndex.TryGetValue(hyperEdgeId.StableIdentity, out var edge)) continue;

                edge.ReinforceTheta(r, EdgeLearningRate, ctx);
                thetaCount++;

                if (edge.ThetaParameters is { } tp)
                    magSum += tp.Theta.Average(Math.Abs);
            }
        }

        // ── Negative path (M5a): all nodes when terminalReward < 0 ──────────
        if (terminalReward < 0.0)
        {
            var allCausalDistances = dag.GetCausalDistances();
            foreach (var node in dag.AllNodes)
            {
                if (node.FiredHyperedgeId is not { } hyperEdgeId) continue;
                if (!TryGetContext(node.Metadata, out var ctx)) continue;

                var pk = new PatternKey(node.State, node.Action);
                allCausalDistances.TryGetValue(pk, out int dist);
                double decayed = terminalReward * Math.Pow(DiscountFactor, dist);

                if (decayed > -ThetaCreditFloor) continue;

                if (!hyperedgeIndex.TryGetValue(hyperEdgeId.StableIdentity, out var edge)) continue;

                double damping = 1.0 - Math.Clamp(NegativeStabilityDamping, 0.0, 1.0) * edge.Confidence;
                double effectiveLr = EdgeLearningRate * Math.Max(1.0, NegativeRateMultiplier) * damping;
                edge.ReinforceTheta(decayed, effectiveLr, ctx);
                negCount++;
            }
        }

        double meanMag = thetaCount > 0 ? magSum / thetaCount : 0.0;
        return (thetaCount, meanMag, negCount, fired);
    }

    internal static bool TryGetContext(
        Dictionary<string, object>? meta,
        out TraversalContext ctx)
    {
        if (meta is not null
            && meta.TryGetValue("ctx_x0", out var x0obj)
            && meta.TryGetValue("ctx_x1", out var x1obj))
        {
            ctx = new TraversalContext(Convert.ToDouble(x0obj), Convert.ToDouble(x1obj));
            return true;
        }
        ctx = default;
        return false;
    }
}
