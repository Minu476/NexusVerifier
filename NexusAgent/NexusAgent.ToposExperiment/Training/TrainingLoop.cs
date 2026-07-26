using RichLearning.V2.Abstractions;
using RichLearning.V2.Memory;
using RichLearning.V2.Models;
using NexusAgent.ToposExperiment.Models;
using NexusAgent.ToposExperiment.Sanity;
using NexusAgent.ToposExperiment.Search;
using NexusAgent.ToposExperiment.Telemetry;
using System.Diagnostics;

namespace NexusAgent.ToposExperiment.Training;

/// <summary>
/// Training loop for the Learned arm. Verbatim logic from
/// <c>NexusAgent.RlbExperiment.Training.TrainingLoop</c>; the ONLY change is the memory
/// parameter type <c>InMemoryGraphMemory</c> → <see cref="IGraphMemory"/> (the loosening the
/// plan calls out — <c>InMemoryGraphMemory</c> is sealed).
///
/// Note: <c>_memory</c> is retained as a field for symmetry with the V2 original even though the
/// loop body doesn't call into it (credit assignment mutates HyperEdge instances directly, via the
/// hyperedge index). Kept so that future additions to the loop that do need the memory don't
/// silently diverge from the V2 baseline's surface.
/// </summary>
internal sealed class TrainingLoop
{
    private readonly IGraphMemory _memory;
    private readonly NexusBackwardChainer _chainer;
    private readonly IReadOnlyDictionary<string, HyperEdge> _hyperedgeIndex;

    public const int DefaultFuel = 200;

    public TrainingLoop(
        IGraphMemory memory,
        NexusBackwardChainer chainer,
        IReadOnlyDictionary<string, HyperEdge> hyperedgeIndex)
    {
        _memory = memory;
        _chainer = chainer;
        _hyperedgeIndex = hyperedgeIndex;
    }

    public async Task<List<EpisodeTelemetry>> RunAsync(
        List<GoalEntry> trainRoots,
        int episodes,
        int seed,
        int fuel = DefaultFuel,
        CancellationToken ct = default)
    {
        var rng   = new Random(seed);
        var goals = trainRoots.ToList();
        var telemetry = new List<EpisodeTelemetry>(episodes);

        int thetaEdgeCountFirstPass = 0;
        int firstPassBoundary = Math.Min(episodes, goals.Count);

        for (int ep = 0; ep < episodes; ep++)
        {
            ct.ThrowIfCancellationRequested();

            var goal = goals[rng.Next(goals.Count)];
            var dag  = new TrajectoryDag();
            var sw   = Stopwatch.StartNew();

            var result = await _chainer.SearchAsync(
                goal.GoalHash, goal.StateVector, SearchArm.Learned, fuel, dag, ct);

            sw.Stop();

            // Post-search BackwardReinforce: library pattern — one call per leaf.
            double terminalReward = result.IsSuccess ? +1.0 : -1.0;
            foreach (var leaf in dag.GetLeafNodes())
                dag.BackwardReinforce(leaf.Id, terminalReward, CreditAssignment.DiscountFactor);

            var (thetaCount, meanMag, negCount, fired) =
                CreditAssignment.ReinforceFromTrajectory(dag, _hyperedgeIndex, terminalReward);

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
