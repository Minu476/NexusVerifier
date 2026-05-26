using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NexusAgent.Core.Llm;
using NexusAgent.Core.Memory;
using NexusAgent.Core.Models;
using NexusAgent.Core.Oracle;

namespace NexusAgent.Core.Agent;

/// <summary>
/// Manages the full episode lifecycle for a single problem. Runs episodes
/// serially (intentional — graph is the shared state; serial avoids write
/// conflicts) and aggregates results.
/// </summary>
public sealed class NexusOrchestrator
{
    private readonly NexusProverSubagent _subagent;
    private readonly ILeanOracle _lean;
    private readonly INeo4jClient _neo4j;
    private readonly TieredLlmRouter _router;
    private readonly ILogger<NexusOrchestrator> _log;

    public NexusOrchestrator(
        NexusProverSubagent subagent,
        ILeanOracle lean,
        INeo4jClient neo4j,
        TieredLlmRouter router,
        ILogger<NexusOrchestrator> log)
    {
        _subagent = subagent;
        _lean = lean;
        _neo4j = neo4j;
        _router = router;
        _log = log;
    }

    public async Task<ProofResult> SolveAsync(
        ProblemInput problem,
        OrchestratorConfig config,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var initialSpend = _router.SpentUsd;

        // Verify the initial sketch parses (catches Lean environment problems early)
        var sanityCheck = await _lean.CompileAsync(problem.InitialSketch, ct);
        if (sanityCheck.Errors.Length > 0 && sanityCheck.Errors[0].Contains("environment", StringComparison.OrdinalIgnoreCase))
        {
            _log.LogError("Lean environment error: {Error}", sanityCheck.Errors[0]);
            return new ProofResult
            {
                ProblemId = problem.Id,
                Outcome = ProofOutcome.LeanEnvironmentError,
                FinalSketch = null,
                EpisodesUsed = 0, TurnsUsed = 0, FossilHits = 0,
                LlmCallsTier1 = 0, LlmCallsTier2 = 0, LlmCallsTier3 = 0,
                EstimatedCostUsd = 0m,
                TotalDuration = sw.Elapsed,
                FossilsCreated = [],
            };
        }

        await _neo4j.UpsertProblemAsync(problem.Id, problem.Source, problem.LeanFilePath, ct);

        var currentSketch = problem.InitialSketch;
        var totalTurns = 0;
        var totalFossilHits = 0;
        var totalByTier = new int[4];

        for (int ep = 0; ep < config.MaxEpisodes; ep++)
        {
            if (sw.Elapsed > config.OverallTimeout)
            {
                _log.LogWarning("Problem {Id} timed out after {Min:F1} min", problem.Id, sw.Elapsed.TotalMinutes);
                return BuildResult(problem.Id, ProofOutcome.TimedOut, null, ep, totalTurns,
                    totalFossilHits, totalByTier, _router.SpentUsd - initialSpend, sw.Elapsed);
            }

            var ctx = new EpisodeContext(
                ProblemId: problem.Id,
                ProblemStatement: problem.Statement,
                DomainTag: problem.DomainTag,
                InitialSketch: currentSketch,
                EpisodeIndex: ep,
                EpisodeId: Guid.NewGuid().ToString("N")[..8],
                MaxTurns: config.MaxTurnsPerEpisode,
                FossilMatchThreshold: config.FossilMatchThreshold,
                FossilDirectSubstituteThreshold: config.FossilDirectSubstituteThreshold);

            using var episodeCts = new CancellationTokenSource(config.EpisodeTimeout);
            using var combined = CancellationTokenSource.CreateLinkedTokenSource(ct, episodeCts.Token);

            EpisodeResult result;
            try
            {
                result = await _subagent.RunEpisodeAsync(ctx, combined.Token);
            }
            catch (OperationCanceledException) when (episodeCts.IsCancellationRequested)
            {
                _log.LogWarning("Episode {Ep} timed out", ep);
                continue;
            }

            totalTurns += result.TurnsUsed;
            totalFossilHits += result.FossilHits;
            for (int i = 0; i < 4; i++) totalByTier[i] += result.LlmCallsByTier[i];

            _log.LogInformation(
                "Episode {Ep}: outcome={Out} turns={T} fossil={F} spent=${Spent:F3}",
                ep, result.Outcome, result.TurnsUsed, result.FossilHits,
                _router.SpentUsd - initialSpend);

            if (result.Outcome == EpisodeOutcome.Solved)
            {
                await _neo4j.MarkProblemSolvedAsync(problem.Id, ep + 1, ct);
                return BuildResult(problem.Id, ProofOutcome.Solved, result.FinalSketch,
                    ep + 1, totalTurns, totalFossilHits, totalByTier,
                    _router.SpentUsd - initialSpend, sw.Elapsed);
            }

            currentSketch = result.FinalSketch;
        }

        return BuildResult(problem.Id, ProofOutcome.EpisodeBudgetExhausted, currentSketch,
            config.MaxEpisodes, totalTurns, totalFossilHits, totalByTier,
            _router.SpentUsd - initialSpend, sw.Elapsed);
    }

    private static ProofResult BuildResult(
        string id, ProofOutcome outcome, string? sketch,
        int episodes, int turns, int fossilHits, int[] byTier,
        decimal cost, TimeSpan duration) => new()
    {
        ProblemId = id,
        Outcome = outcome,
        FinalSketch = sketch,
        EpisodesUsed = episodes,
        TurnsUsed = turns,
        FossilHits = fossilHits,
        LlmCallsTier1 = byTier[1],
        LlmCallsTier2 = byTier[2],
        LlmCallsTier3 = byTier[3],
        EstimatedCostUsd = cost,
        TotalDuration = duration,
        FossilsCreated = [],
    };
}

public sealed record ProblemInput(
    string Id,
    string Source,        // "OEIS" | "Erdos"
    string DomainTag,
    string LeanFilePath,
    string Statement,
    string InitialSketch);

public sealed record OrchestratorConfig
{
    public int MaxEpisodes { get; init; } = 100;
    public int MaxTurnsPerEpisode { get; init; } = 20;
    public TimeSpan EpisodeTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan OverallTimeout { get; init; } = TimeSpan.FromHours(2);
    public float FossilMatchThreshold { get; init; } = 0.75f;
    public float FossilDirectSubstituteThreshold { get; init; } = 0.90f;
}
