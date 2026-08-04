using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NexusAgent.Core.Llm;
using NexusAgent.Core.Memory;
using NexusAgent.Core.Models;
using NexusAgent.Core.Oracle;
using NexusAgent.Core.Planning;
using NexusAgent.Core.Prompts;

namespace NexusAgent.Core.Agent;

/// <summary>
/// Manages the full episode lifecycle for a single problem. Runs episodes
/// serially (intentional — graph is the shared state; serial avoids write
/// conflicts) and aggregates results.
/// </summary>
public sealed class NexusOrchestrator
{
    private readonly NexusProverSubagent _subagent;
    private readonly BestFirstGraphPlanner _planner;
    private readonly ILeanOracle _lean;
    private readonly INeo4jClient _neo4j;
    private readonly TieredLlmRouter _router;
    private readonly PromptBuilder _promptBuilder;
    private readonly ILogger<NexusOrchestrator> _log;

    public NexusOrchestrator(
        NexusProverSubagent subagent,
        BestFirstGraphPlanner planner,
        ILeanOracle lean,
        INeo4jClient neo4j,
        TieredLlmRouter router,
        PromptBuilder promptBuilder,
        ILogger<NexusOrchestrator> log)
    {
        _subagent = subagent;
        _planner = planner;
        _lean = lean;
        _neo4j = neo4j;
        _router = router;
        _promptBuilder = promptBuilder;
        _log = log;
    }

    public async Task<ProofResult> SolveAsync(
        ProblemInput problem,
        OrchestratorConfig config,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var totalCost = 0m;

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
                EpisodesUsed = 0, TurnsUsed = 0, FossilHits = 0, FossilRetrievalSamples = 0, GraphReplayHits = 0,
                LlmCallsTier1 = 0, LlmCallsTier2 = 0, LlmCallsTier3 = 0,
                EstimatedCostUsd = 0m,
                TotalDuration = sw.Elapsed,
                FossilsCreated = [],
                AvgFossilSimilarity = 0f,
                BestMissedFossilSim = 0f,
                StructuralGateRejections = 0,
                GraphPlannerUsed = false,
                GraphPlannerExpansions = 0,
                GraphPlannerAcceptedTransitions = 0,
                LegacyLlmFallbackUsed = false,
            };
        }

        await _neo4j.UpsertProblemAsync(problem.Id, problem.Source, problem.LeanFilePath, ct);

        // One goal-graph per SolveAsync call, alive across all its episodes, dies
        // with the call — no persistence across separate CLI invocations (deferred,
        // see docs/plan). Local variable, not a field: this class and
        // NexusProverSubagent are DI singletons that `nexus bench` calls
        // concurrently across different problems, so any per-problem state must
        // live on the call stack, never as instance/static state.
        var goalGraph = new ProofGoalGraph();

        var plannerUsed = false;
        var plannerExpansions = 0;
        var plannerAcceptedTransitions = 0;
        var legacyFallbackUsed = false;

        if (config.UseGraphFirstPlanner)
        {
            plannerUsed = true;
            var plannerRun = await _planner.TrySolveAsync(problem, config, ct);
            plannerExpansions = plannerRun.Expansions;
            plannerAcceptedTransitions = plannerRun.AcceptedTransitions;

            if (plannerRun.Solved)
            {
                await _neo4j.MarkProblemSolvedAsync(problem.Id, 1, ct);
                return new ProofResult
                {
                    ProblemId = problem.Id,
                    Outcome = ProofOutcome.Solved,
                    FinalSketch = plannerRun.FinalSketch,
                    EpisodesUsed = 1,
                    TurnsUsed = plannerRun.Expansions,
                    FossilHits = 0,
                    FossilRetrievalSamples = 0,
                    LlmCallsTier1 = 0,
                    LlmCallsTier2 = 0,
                    LlmCallsTier3 = 0,
                    EstimatedCostUsd = 0m,
                    TotalDuration = sw.Elapsed,
                    FossilsCreated = [],
                    AvgFossilSimilarity = 0f,
                    BestMissedFossilSim = 0f,
                    GraphReplayHits = 0,
                    StructuralGateRejections = 0,
                    GraphPlannerUsed = plannerUsed,
                    GraphPlannerExpansions = plannerExpansions,
                    GraphPlannerAcceptedTransitions = plannerAcceptedTransitions,
                    LegacyLlmFallbackUsed = false,
                };
            }

            if (!config.UseLegacyLlmProverFallback)
            {
                return new ProofResult
                {
                    ProblemId = problem.Id,
                    Outcome = ProofOutcome.EpisodeBudgetExhausted,
                    FinalSketch = plannerRun.FinalSketch,
                    EpisodesUsed = 1,
                    TurnsUsed = plannerRun.Expansions,
                    FossilHits = 0,
                    FossilRetrievalSamples = 0,
                    LlmCallsTier1 = 0,
                    LlmCallsTier2 = 0,
                    LlmCallsTier3 = 0,
                    EstimatedCostUsd = 0m,
                    TotalDuration = sw.Elapsed,
                    FossilsCreated = [],
                    AvgFossilSimilarity = 0f,
                    BestMissedFossilSim = 0f,
                    GraphReplayHits = 0,
                    StructuralGateRejections = 0,
                    GraphPlannerUsed = plannerUsed,
                    GraphPlannerExpansions = plannerExpansions,
                    GraphPlannerAcceptedTransitions = plannerAcceptedTransitions,
                    LegacyLlmFallbackUsed = false,
                };
            }

            legacyFallbackUsed = true;
            _log.LogInformation(
                "Problem {Id}: graph planner exhausted frontier (expansions={E}, accepted={A}) — falling back to legacy LLM loop",
                problem.Id, plannerExpansions, plannerAcceptedTransitions);
            problem = problem with { InitialSketch = plannerRun.FinalSketch };
        }

        var currentSketch = problem.InitialSketch;
        // Track the best partial proof seen across all episodes (fewest sorries).
        // This is passed as InitialSketch to subsequent episodes so good progress
        // is not discarded at episode boundaries. Worst case: no change (bestSorryCount
        // stays at initialSorryCount and currentSketch is always the original).
        var bestSketch = problem.InitialSketch;
        var bestSorryCount = sanityCheck.SorryCount;
        var totalTurns = 0;
        var totalFossilHits = 0;
        var totalFossilRetrievalSamples = 0;
        var totalGraphReplayHits = 0;
        var totalStructuralGateRejections = 0;
        var totalByTier = new int[4];
        var simSum = 0f;
        var simCount = 0;
        var bestMissed = 0f;
        var totalTier075Telemetry = new GraphProposalTelemetry();
        // Cross-episode cycle abort: if bestSorryCount does not improve for this many
        // consecutive episodes, the agent is stuck — abort remaining episodes early.
        var stuckEpisodes = 0;
        const int MaxStuckEpisodes = 2;
        // V2 DetectCycleInTrajectoryAsync pattern: pure in-memory HashSet scan over the
        // per-episode landmark ID sequence. Catches true cycles (agent returning to the
        // exact same proof state across episodes) independently of sorry-count changes.
        var episodeLandmarkIds = new List<string>();

        for (int ep = 0; ep < config.MaxEpisodes; ep++)
        {
            if (sw.Elapsed > config.OverallTimeout)
            {
                _log.LogWarning("Problem {Id} timed out after {Min:F1} min", problem.Id, sw.Elapsed.TotalMinutes);
                if (bestMissed >= 0.85f)
                    _log.LogWarning(
                        "Problem {Id} timed out — near-miss: vault sim={Sim:F3} (below substitute threshold)",
                        problem.Id, bestMissed);
                return BuildResult(problem.Id, ProofOutcome.TimedOut, null, ep, totalTurns,
                    totalFossilHits, totalFossilRetrievalSamples, totalGraphReplayHits, totalByTier, totalCost, sw.Elapsed,
                    simCount > 0 ? simSum / simCount : 0f, bestMissed, totalStructuralGateRejections,
                    plannerUsed, plannerExpansions, plannerAcceptedTransitions, legacyFallbackUsed,
                    goalGraphDedupHits: goalGraph.DedupHits);
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
                FossilDirectSubstituteThreshold: config.FossilDirectSubstituteThreshold,
                GoalGraph: goalGraph,
                PivotGatesEnabled: config.PivotGatesEnabled,
                MaxDiagnosesPerEpisode: config.MaxDiagnosesPerEpisode);

            using var episodeCts = new CancellationTokenSource(config.EpisodeTimeout);
            using var combined = CancellationTokenSource.CreateLinkedTokenSource(ct, episodeCts.Token);

            EpisodeResult result;
            try
            {
                result = await _subagent.RunEpisodeAsync(ctx, combined.Token);
            }
            catch (OperationCanceledException) when (episodeCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                _log.LogWarning("Episode {Ep} timed out", ep);
                continue;
            }

            totalTurns += result.TurnsUsed;
            totalFossilHits += result.FossilHits;
            totalFossilRetrievalSamples += result.FossilRetrievalSamples;
            totalGraphReplayHits += result.GraphReplayHits;
            for (int i = 0; i < 4; i++) totalByTier[i] += result.LlmCallsByTier[i];
            totalCost += result.CostUsd;
            if (result.AvgFossilSimilarity > 0) { simSum += result.AvgFossilSimilarity; simCount++; }
            if (result.BestMissedFossilSim > bestMissed) bestMissed = result.BestMissedFossilSim;
            if (result.Outcome == EpisodeOutcome.StructuralGateRejection) totalStructuralGateRejections++;
            totalTier075Telemetry.MergeFrom(result.Tier075Telemetry);

            _log.LogInformation(
                "Episode {Ep}: outcome={Out} turns={T} fossil={F} replay={R} avgSim={AvgSim:F3} spent=${Spent:F3}",
                ep, result.Outcome, result.TurnsUsed, result.FossilHits, result.GraphReplayHits,
                result.AvgFossilSimilarity, result.CostUsd);

            if (result.Outcome == EpisodeOutcome.Solved)
            {
                await _neo4j.MarkProblemSolvedAsync(problem.Id, ep + 1, ct);
                return BuildResult(problem.Id, ProofOutcome.Solved, result.FinalSketch,
                    ep + 1, totalTurns, totalFossilHits, totalFossilRetrievalSamples, totalGraphReplayHits, totalByTier,
                    totalCost, sw.Elapsed,
                    simCount > 0 ? simSum / simCount : 0f, bestMissed, totalStructuralGateRejections,
                    plannerUsed, plannerExpansions, plannerAcceptedTransitions, legacyFallbackUsed,
                    totalTier075Telemetry, goalGraphDedupHits: goalGraph.DedupHits);
            }

            // Carry the best sketch (fewest sorries) forward, not just the last one.
            // If Ep0 regressed from 1 sorry back to 2 at MaxTurns, Ep1 still starts
            // from the 1-sorry state that we saw mid-episode (or wherever the episode
            // closed with the fewest sorries).
            //
            // Defense-in-depth: apply structural gate before accepting any improvement.
            // The primary gate is subagent step 5b; this catches any possible bypass
            // (e.g., graph-replay paths not checked inline).
            var hasImprovement = result.FinalSorryCount < bestSorryCount
                && result.FinalSketch is not null
                && SketchValidator.IsStructurallyValid(problem.InitialSketch, result.FinalSketch);
            if (result.FinalSorryCount < bestSorryCount
                && result.FinalSketch is not null
                && !hasImprovement)
            {
                _log.LogWarning(
                    "Episode {Ep}: orchestrator structural gate rejected improvement — " +
                    "partial reward hacking bypassed subagent gate",
                    ep);
            }
            if (hasImprovement)
            {
                var prev = bestSorryCount;
                bestSorryCount = result.FinalSorryCount;
                bestSketch = result.FinalSketch;
                _log.LogInformation(
                    "Episode {Ep}: new best sketch — {N} sorries (was {Prev})",
                    ep, bestSorryCount, prev);
                stuckEpisodes = 0;
            }
            else
            {
                stuckEpisodes++;
                if (stuckEpisodes >= MaxStuckEpisodes)
                {
                    // ── Between-episode reformulation (per OAI walkthroughs) ──────────────
                    // Instead of aborting, force the model to diagnose why this *approach*
                    // failed and produce a genuinely different proof strategy. The next episode
                    // starts from the reformulated sketch, not a blind fresh attempt at the same
                    // style. This is the diagnose-then-pivot discipline every frontier proof in
                    // the walkthroughs used — the win is in changing the approach, not grinding
                    // harder on one.
                    //
                    // Only attempt if there are remaining episodes AND pivot gates are enabled;
                    // otherwise abort as before.
                    if (ep + 1 < config.MaxEpisodes && bestSketch is not null && config.PivotGatesEnabled)
                    {
                        _log.LogInformation(
                            "Problem {Id}: {N} consecutive stuck episodes — invoking between-episode reformulation (approach pivot)",
                            problem.Id, stuckEpisodes);

                        var reformCtx = new RouterContext
                        {
                            EpisodeIndex = ep,
                            TurnIndex = config.MaxTurnsPerEpisode,  // force Tier 3 for the pivot
                            TurnsSinceLastProgress = config.MaxTurnsPerEpisode,
                            CurrentSorryCount = bestSorryCount,
                        };

                        // Reformulation-retry loop: if the first attempt produces non-compiling
                        // Lean, feed the compile error back and retry once (same pattern as the
                        // prover turn loop). This addresses the Grimm case where the model
                        // produced a genuinely different approach but with a syntax error.
                        string? acceptedSketch = null;
                        int acceptedSorry = -1;
                        const int maxReformRetries = 2;  // 1 initial + 1 retry with error feedback

                        for (int reformAttempt = 0; reformAttempt < maxReformRetries; reformAttempt++)
                        {
                            var reformRequest = reformAttempt == 0
                                ? _promptBuilder.BuildReformulationRequest(
                                    problem.Statement, bestSketch, bestSorryCount, ep + 1)
                                : _promptBuilder.BuildReformulationRequest(
                                    problem.Statement, bestSketch, bestSorryCount, ep + 1);

                            try
                            {
                                var reformResponse = await _router.SendAsync(reformCtx, reformRequest, ct);
                                totalCost += reformResponse.EstimatedCostUsd;

                                var newSketch = PromptBuilder.ExtractLeanFromResponse(reformResponse.Content);
                                if (string.IsNullOrWhiteSpace(newSketch))
                                {
                                    _log.LogWarning(
                                        "Problem {Id}: reformulation attempt {A} produced no Lean code {Note}",
                                        problem.Id, reformAttempt + 1,
                                        reformAttempt == 0 ? "— model may have returned natural-language analysis only" : "");
                                    break;  // no point retrying if the model won't produce code
                                }
                                if (!SketchValidator.IsStructurallyValid(problem.InitialSketch, newSketch))
                                {
                                    _log.LogWarning(
                                        "Problem {Id}: reformulation attempt {A} — structural gate rejected (declaration modified)",
                                        problem.Id, reformAttempt + 1);
                                    break;
                                }

                                var reformCompile = await _lean.CompileAsync(newSketch, ct);
                                if (reformCompile.Compiled)
                                {
                                    acceptedSketch = newSketch;
                                    acceptedSorry = reformCompile.SorryCount;
                                    _log.LogInformation(
                                        "Problem {Id}: reformulation accepted (attempt {A}) — new sketch with {S} sorry, starting fresh approach",
                                        problem.Id, reformAttempt + 1, reformCompile.SorryCount);
                                    break;
                                }
                                else if (reformAttempt < maxReformRetries - 1)
                                {
                                    _log.LogInformation(
                                        "Problem {Id}: reformulation attempt {A} did not compile — retrying with error feedback",
                                        problem.Id, reformAttempt + 1);
                                    // TODO: feed reformCompile.Errors back into the next attempt.
                                    // For now, retry with the same prompt (the model may produce
                                    // different output at temp 0.7); a proper error-feedback loop
                                    // requires extending BuildReformulationRequest's signature.
                                }
                                else
                                {
                                    _log.LogWarning(
                                        "Problem {Id}: reformulation did not compile after {A} attempt(s) — aborting",
                                        problem.Id, reformAttempt + 1);
                                }
                            }
                            catch (Exception ex)
                            {
                                _log.LogWarning(ex,
                                    "Problem {Id}: reformulation attempt {A} failed — aborting", problem.Id, reformAttempt + 1);
                                break;
                            }
                        }

                        if (acceptedSketch is not null)
                        {
                            currentSketch = acceptedSketch;
                            stuckEpisodes = 0;
                            continue;
                        }
                    }

                    _log.LogWarning(
                        "Problem {Id}: {N} consecutive episodes with no improvement — aborting remaining episodes",
                        problem.Id, stuckEpisodes);
                    return BuildResult(problem.Id, ProofOutcome.Aborted, currentSketch,
                        ep + 1, totalTurns, totalFossilHits, totalFossilRetrievalSamples, totalGraphReplayHits, totalByTier,
                        totalCost, sw.Elapsed,
                        simCount > 0 ? simSum / simCount : 0f, bestMissed, totalStructuralGateRejections,
                        plannerUsed, plannerExpansions, plannerAcceptedTransitions, legacyFallbackUsed,
                        totalTier075Telemetry, goalGraphDedupHits: goalGraph.DedupHits);
                }
            }
            currentSketch = bestSketch;

            // V2 DetectCycleInTrajectoryAsync pattern — pure in-memory HashSet scan.
            // Compute the landmark ID of the current best state and check for revisit.
            var epLandmarkId = ProofCartographer.ComputeLandmarkId(
                bestSketch, bestSorryCount, problem.Id);
            episodeLandmarkIds.Add(epLandmarkId);
            var landmarkSeen = new HashSet<string>();
            foreach (var lid in episodeLandmarkIds)
            {
                if (!landmarkSeen.Add(lid))
                {
                    _log.LogWarning(
                        "Problem {Id}: landmark cycle detected at episode {Ep} — aborting",
                        problem.Id, ep);
                    return BuildResult(problem.Id, ProofOutcome.Aborted, currentSketch,
                        ep + 1, totalTurns, totalFossilHits, totalFossilRetrievalSamples, totalGraphReplayHits, totalByTier,
                        totalCost, sw.Elapsed,
                        simCount > 0 ? simSum / simCount : 0f, bestMissed, totalStructuralGateRejections,
                        plannerUsed, plannerExpansions, plannerAcceptedTransitions, legacyFallbackUsed,
                        totalTier075Telemetry, goalGraphDedupHits: goalGraph.DedupHits);
                }
            }
        }

        if (bestMissed >= 0.85f)
            _log.LogWarning(
                "Problem {Id} FAILED — near-miss: vault had fossil at sim={Sim:F3} but below substitute threshold. " +
                "Potential encoder misranking — review vault for this subgoal pattern.",
                problem.Id, bestMissed);
        else if (bestMissed > 0)
            _log.LogInformation(
                "Problem {Id} failed — best vault proximity: sim={Sim:F3}",
                problem.Id, bestMissed);

        return BuildResult(problem.Id, ProofOutcome.EpisodeBudgetExhausted, currentSketch,
            config.MaxEpisodes, totalTurns, totalFossilHits, totalFossilRetrievalSamples, totalGraphReplayHits, totalByTier,
            totalCost, sw.Elapsed,
            simCount > 0 ? simSum / simCount : 0f, bestMissed, totalStructuralGateRejections,
            plannerUsed, plannerExpansions, plannerAcceptedTransitions, legacyFallbackUsed,
            totalTier075Telemetry, goalGraphDedupHits: goalGraph.DedupHits);
    }

    private static ProofResult BuildResult(
        string id, ProofOutcome outcome, string? sketch,
        int episodes, int turns, int fossilHits, int fossilRetrievalSamples, int graphReplayHits, int[] byTier,
        decimal cost, TimeSpan duration, float avgFossilSim, float bestMissed,
        int structuralGateRejections,
        bool graphPlannerUsed,
        int graphPlannerExpansions,
        int graphPlannerAcceptedTransitions,
        bool legacyLlmFallbackUsed,
        GraphProposalTelemetry? tier075Telemetry = null,
        int goalGraphDedupHits = 0) => new()
    {
        ProblemId = id,
        Outcome = outcome,
        FinalSketch = sketch,
        EpisodesUsed = episodes,
        TurnsUsed = turns,
        FossilHits = fossilHits,
        FossilRetrievalSamples = fossilRetrievalSamples,
        GraphReplayHits = graphReplayHits,
        LlmCallsTier1 = byTier[1],
        LlmCallsTier2 = byTier[2],
        LlmCallsTier3 = byTier[3],
        EstimatedCostUsd = cost,
        TotalDuration = duration,
        FossilsCreated = [],
        AvgFossilSimilarity = avgFossilSim,
        BestMissedFossilSim = bestMissed,
        StructuralGateRejections = structuralGateRejections,
        GraphPlannerUsed = graphPlannerUsed,
        GraphPlannerExpansions = graphPlannerExpansions,
        GraphPlannerAcceptedTransitions = graphPlannerAcceptedTransitions,
        LegacyLlmFallbackUsed = legacyLlmFallbackUsed,
        Tier075Telemetry = tier075Telemetry ?? new GraphProposalTelemetry(),
        GoalGraphDedupHits = goalGraphDedupHits,
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
    public bool UseGraphFirstPlanner { get; init; } = false;
    public bool UseLegacyLlmProverFallback { get; init; } = true;
    public int PlannerMaxExpansions { get; init; } = 48;
    public int PlannerBranchFactor { get; init; } = 8;
    public int PlannerNeighborK { get; init; } = 12;
    public int PlannerStateVisitCap { get; init; } = 3;
    public float PlannerDepthWeight { get; init; } = 0.10f;
    public float PlannerRankWeight { get; init; } = 1.00f;
    public float PlannerSuccessWeight { get; init; } = 0.75f;
    public float PlannerBranchingWeight { get; init; } = 0.35f;
    public float PlannerErrorWeight { get; init; } = 0.50f;
    public float PlannerNoveltyBonus { get; init; } = 0.30f;
    /// <summary>When true, obstruction-diagnosis + between-episode reformulation gates are
    /// active. When false, both revert to original abort behavior (for A/B testing).</summary>
    public bool PivotGatesEnabled { get; init; } = true;
    /// <summary>Safety cap: max obstruction-diagnosis calls per episode. Each diagnosis resets
    /// the consecutive-structural-violation counter, so without a cap a model could alternate
    /// reward-hacking and diagnosis indefinitely within an episode. Once exhausted, a further
    /// consecutive violation aborts the episode instead of diagnosing again.</summary>
    public int MaxDiagnosesPerEpisode { get; init; } = 1;
}
