using NexusAgent.ToposExperiment.Models;
using NexusAgent.ToposExperiment.Sanity;
using NexusAgent.ToposExperiment.Telemetry;
using NexusAgent.ToposExperiment.Training;
using RichLearning.V2.Memory;
using System.Diagnostics;
using Topos.Hypergraph;

namespace NexusAgent.ToposExperiment.NarySearch;

/// <summary>
/// Training loop for the n-ary chainer. Mirrors <see cref="Training.TrainingLoop"/>'s shape —
/// per episode: run the chainer, BackwardReinforce from each leaf, then credit assignment — but
/// over the Topos-native chainer and credit paths. No <c>IGraphMemory</c>, no <c>HyperEdge</c>
/// index; the kernel is the single source of truth for both search and learning.
/// </summary>
internal sealed class NaryTrainingLoop
{
    private readonly NaryBackwardChainer _chainer;
    private readonly HypergraphKernel _kernel;

    public const int DefaultFuel = 200;

    public NaryTrainingLoop(NaryBackwardChainer chainer, HypergraphKernel kernel)
    {
        _chainer = chainer;
        _kernel = kernel;
    }

    public async Task<List<EpisodeTelemetry>> RunAsync(
        List<GoalEntry> trainRoots,
        int episodes,
        int seed,
        int fuel = DefaultFuel,
        CancellationToken ct = default)
    {
        var rng = new Random(seed);
        var goals = trainRoots.ToList();
        var telemetry = new List<EpisodeTelemetry>(episodes);

        int thetaEdgeCountFirstPass = 0;
        int firstPassBoundary = Math.Min(episodes, goals.Count);

        for (int ep = 0; ep < episodes; ep++)
        {
            ct.ThrowIfCancellationRequested();

            var goal = goals[rng.Next(goals.Count)];
            var dag = new TrajectoryDag();
            var sw = Stopwatch.StartNew();

            var result = await _chainer.SearchAsync(
                goal.GoalHash, goal.StateVector, NarySearchArm.Learned, fuel, dag, ct);

            sw.Stop();

            double terminalReward = result.IsSuccess ? +1.0 : -1.0;
            foreach (var leaf in dag.GetLeafNodes())
                dag.BackwardReinforce(leaf.Id, terminalReward, ToposNativeCreditAssignment.DiscountFactor);

            var (thetaCount, meanMag, negCount, fired) =
                ToposNativeCreditAssignment.ReinforceFromTrajectory(dag, _kernel, terminalReward);

            telemetry.Add(new EpisodeTelemetry(
                EpisodeId:            Guid.NewGuid().ToString("N")[..12],
                TheoremId:            goal.TheoremId,
                GoalHash:             goal.GoalHash,
                Arm:                  "Learned",
                Solved:               result.IsSuccess,
                Steps:                result.Steps,
                ThetaEdgesReinforced: thetaCount,
                MeanThetaMagnitude:   meanMag,
                HyperedgesFired:      fired,
                NegativeReinforced:   negCount,
                ElapsedMs:            sw.Elapsed.TotalMilliseconds));

            thetaEdgeCountFirstPass += thetaCount;

            if (ep == firstPassBoundary - 1)
                SanityTripwire.Assert(thetaEdgeCountFirstPass, firstPassBoundary);
        }

        return telemetry;
    }
}
