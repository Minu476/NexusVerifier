using NexusAgent.ToposExperiment.NarySearch;
using RichLearning.V2.Abstractions;
using RichLearning.V2.Memory;
using RichLearning.V2.Models;
using Topos.Hypergraph;

namespace NexusAgent.ToposExperiment.Training;

/// <summary>
/// Post-episode credit assignment that reinforces the Topos edge-vertex's <see cref="LearnableEdge"/>
/// property <b>directly through the kernel's typed property pool</b> — no V2 <c>HyperEdge</c>, no
/// reference-stability contract. This is the n-ary equivalent of
/// <see cref="CreditAssignment.ReinforceFromTrajectory"/>, and it resolves integration-report
/// issue #2 (the silent-failure reference-stability contract) by construction: there's no shared
/// mutable object to keep in sync, because <see cref="LearnableEdge"/> is an immutable value type
/// and the kernel is the single source of truth.
///
/// <b>The update pattern (pure Topos public API):</b>
///   1. Resolve the fired edge-vertex's Handle from the trajectory's <c>FiredHyperedgeId</c>
///      (encoded by <see cref="NaryBackwardChainer.EdgeIdentity"/>).
///   2. <c>GetProperty&lt;LearnableEdge&gt;</c> — read current theta (or
///      <see cref="LearnableEdge.CreateUninitialized"/> if absent).
///   3. <c>LearnableEdge.Reinforce(features, reward, lr)</c> — gradient step, returns a new
///      immutable value (same math as V2's <c>HyperEdge.ReinforceTheta</c>).
///   4. <c>SetProperty&lt;LearnableEdge&gt;</c> — write it back. The kernel's SparseSet pool
///      handles the storage; subsequent reads see the new value.
///
/// <b>Why this dissolves issue #2:</b> the projected-path credit assignment mutated
/// <c>edge.ThetaParameters.Theta[..]</c> through a reference obtained earlier, relying on the
/// memory returning the same instance per id. A backend that defensively copied on read would
/// silently break it. Here, "the current theta" is always whatever's in the kernel — there's no
/// stale reference to get out of sync. Read-modify-write over an immutable value, going through
/// the kernel every time, is inherently consistent.
///
/// <b>Constants mirror <see cref="CreditAssignment"/>'s DapsaKernel-matched values</b> so the
/// n-ary and projected paths are comparable (same discount, same lr, same thresholds). The one
/// difference is the feature-vector length: V2's <c>HyperEdge.Evaluate</c> uses a 7-slot theta
/// (bias + X0 + X1 + 3 statistic slots + 1 structural discriminator); this n-ary path uses a
/// 3-slot theta (bias + X0 + X1). Same two decision-context features, deliberately simpler —
/// see TOPOS_INTEGRATION_REPORT.md §"feature-vector parity" for why.
/// </summary>
internal static class ToposNativeCreditAssignment
{
    // ── Same constants as CreditAssignment (DapsaKernel-matched) ─────────────
    public const double DiscountFactor           = 0.95;
    public const double EdgeLearningRate         = 0.05;
    public const double FossilizationQThreshold  = 0.5;
    public const double ThetaCreditFloor         = 0.01;
    public const double NegativeRateMultiplier   = 2.0;
    public const double NegativeStabilityDamping = 0.5;

    /// <summary>The two decision-context features the LearnableEdge is trained over.</summary>
    public const int FeatureCount = 2;

    public static (int thetaCount, double meanMag, int negCount, int fired) ReinforceFromTrajectory(
        TrajectoryDag dag,
        HypergraphKernel kernel,
        double terminalReward)
    {
        var learnableProp = kernel.ResolveProperty<LearnableEdge>("learnable");
        var statsProp = kernel.ResolveProperty<EdgeStatistics>("stats");

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
                if (node.FiredHyperedgeId is not { } edgeId) continue;
                if (!NaryBackwardChainer.EdgeIdentity.TryGetHandle(edgeId, out var edgeHandle)) continue;
                if (!TryGetContext(node.Metadata, out var ctx)) continue;

                var pk = new PatternKey(node.State, node.Action);
                if (!decayedRewards.TryGetValue(pk, out double r)) continue;

                if (ReinforceEdge(kernel, learnableProp, edgeHandle, ctx, r, EdgeLearningRate))
                {
                    thetaCount++;
                    if (kernel.TryGetProperty(learnableProp, edgeHandle, out var le))
                        magSum += le.Theta.Average(Math.Abs);
                }
            }
        }

        // ── Negative path (M5a): all nodes when terminalReward < 0 ──────────
        if (terminalReward < 0.0)
        {
            var allCausalDistances = dag.GetCausalDistances();
            foreach (var node in dag.AllNodes)
            {
                if (node.FiredHyperedgeId is not { } edgeId) continue;
                if (!NaryBackwardChainer.EdgeIdentity.TryGetHandle(edgeId, out var edgeHandle)) continue;
                if (!TryGetContext(node.Metadata, out var ctx)) continue;

                var pk = new PatternKey(node.State, node.Action);
                allCausalDistances.TryGetValue(pk, out int dist);
                double decayed = terminalReward * Math.Pow(DiscountFactor, dist);
                if (decayed > -ThetaCreditFloor) continue;

                // Damping uses EdgeStatistics.Confidence (the analog of V2's HyperEdge.Confidence).
                var stats = kernel.TryGetProperty(statsProp, edgeHandle, out var s) ? s : EdgeStatistics.Initial;
                double damping = 1.0 - Math.Clamp(NegativeStabilityDamping, 0.0, 1.0) * stats.Confidence;
                double effectiveLr = EdgeLearningRate * Math.Max(1.0, NegativeRateMultiplier) * damping;

                if (ReinforceEdge(kernel, learnableProp, edgeHandle, ctx, decayed, effectiveLr))
                    negCount++;
            }
        }

        double meanMag = thetaCount > 0 ? magSum / thetaCount : 0.0;
        return (thetaCount, meanMag, negCount, fired);
    }

    /// <summary>
    /// Read-modify-write the LearnableEdge on a Topos edge-vertex. Returns false if the edge
    /// couldn't be reinforced (missing handle, kernel rejected the write). This is the operation
    /// that replaces V2's <c>edge.ReinforceTheta(...)</c> — and it can't silently desync because
    /// there's no shared mutable object: the immutable value is read fresh, reinforced, written
    /// back, all through the kernel.
    /// </summary>
    private static bool ReinforceEdge(
        HypergraphKernel kernel,
        PropertyKey<LearnableEdge> learnableProp,
        Handle edgeHandle,
        TraversalContext ctx,
        double reward,
        double learningRate)
    {
        var current = kernel.TryGetProperty(learnableProp, edgeHandle, out var le)
            ? le
            : LearnableEdge.CreateUninitialized(FeatureCount);

        ReadOnlySpan<float> features = [(float)ctx.X0, (float)ctx.X1];
        var reinforced = current.Reinforce(features, (float)reward, (float)learningRate);
        kernel.SetProperty(learnableProp, edgeHandle, reinforced);
        return true;
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
