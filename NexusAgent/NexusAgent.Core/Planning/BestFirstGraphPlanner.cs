using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using NexusAgent.Core.Agent;
using NexusAgent.Core.Encoding;
using NexusAgent.Core.Memory;
using NexusAgent.Core.Models;
using NexusAgent.Core.Oracle;

namespace NexusAgent.Core.Planning;

/// <summary>
/// Deterministic graph-first planner over Neo4j proposals with Lean-validated transitions.
/// Search priority is expected closure cost (not raw vector similarity), with hard pruning
/// guards to keep expansion bounded.
/// </summary>
public sealed class BestFirstGraphPlanner
{
    private readonly INeo4jClient _neo4j;
    private readonly ILeanOracle _lean;
    private readonly ProofStateEncoder _encoder;
    private readonly HyperedgeComposer? _composer;
    private readonly ILogger<BestFirstGraphPlanner> _log;

    public BestFirstGraphPlanner(
        INeo4jClient neo4j,
        ILeanOracle lean,
        ProofStateEncoder encoder,
        HyperedgeComposer composer,
        ILogger<BestFirstGraphPlanner> log)
    {
        _neo4j    = neo4j;
        _lean     = lean;
        _encoder  = encoder;
        _composer = composer;
        _log      = log;
    }

    public async Task<PlannerRunResult> TrySolveAsync(
        ProblemInput problem,
        OrchestratorConfig config,
        CancellationToken ct)
    {
        var initialResult = await _lean.CompileAsync(problem.InitialSketch, ct);
        if (initialResult.IsFullyProved)
        {
            return new PlannerRunResult
            {
                Solved = true,
                FinalSketch = problem.InitialSketch,
                FinalSorryCount = 0,
                Expansions = 0,
                AcceptedTransitions = 0,
                FrontierCollapsed = false,
            };
        }

        var initialState = BuildState(problem.InitialSketch, initialResult, problem.DomainTag);
        var initialHash = _encoder.ComputeCanonicalStateHash(initialState);

        var bestSketch = problem.InitialSketch;
        var bestSorryCount = initialResult.SorryCount;

        var frontier = new PriorityQueue<PlannerNode, float>();
        frontier.Enqueue(new PlannerNode(problem.InitialSketch, initialResult, initialState, initialHash, Depth: 0),
            priority: initialResult.SorryCount);

        var seen = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [initialHash] = initialResult.SorryCount,
        };
        var stateVisitCount = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [initialHash] = 1,
        };

        var expansions = 0;
        var acceptedTransitions = 0;
        var compileRejects = 0;
        var structuralRejects = 0;
        var cyclePrunes = 0;
        var duplicatePrunes = 0;

        while (frontier.Count > 0 && expansions < config.PlannerMaxExpansions)
        {
            ct.ThrowIfCancellationRequested();
            var node = frontier.Dequeue();

            if (stateVisitCount.TryGetValue(node.CanonicalHash, out var visits)
                && visits > config.PlannerStateVisitCap)
            {
                cyclePrunes++;
                continue;
            }

            expansions++;

            // Tier 0: AND-join composition from the hyperedge store.
            // Checks whether the first open goal can be closed directly from stored
            // HyperedgeRecords without any LLM call. Lean compilation acts as the gate.
            if (_composer is not null && node.Lean.PendingGoalTexts.Length > 0)
            {
                var firstGoal = node.Lean.PendingGoalTexts[0];
                var derivation = await _composer.TryComposeAsync(firstGoal, maxDepth: 2, ct);
                if (derivation is not null)
                {
                    var composedTactic  = HyperedgeComposer.BuildTacticSketch(derivation);
                    var composedSketch  = SubstituteFirstSorry(node.Sketch, composedTactic);
                    var composedResult  = await _lean.CompileAsync(composedSketch, ct);

                    if (composedResult.IsFullyProved)
                    {
                        _log.LogInformation(
                            "[HyperedgeComposer] Solved {Id} at expansion {E} " +
                            "(derivation depth {D}, tactic: {T})",
                            problem.Id, expansions, derivation.Depth, composedTactic);
                        return new PlannerRunResult
                        {
                            Solved               = true,
                            FinalSketch          = composedSketch,
                            FinalSorryCount      = 0,
                            Expansions           = expansions,
                            AcceptedTransitions  = acceptedTransitions + 1,
                            FrontierCollapsed    = false,
                            CompileRejects       = compileRejects,
                            StructuralRejects    = structuralRejects,
                            CyclePrunes          = cyclePrunes,
                            DuplicatePrunes      = duplicatePrunes,
                        };
                    }

                    // Partially useful: reduces sorry count — push to frontier with
                    // high priority so it is expanded before unguided proposals.
                    if (composedResult.Compiled &&
                        composedResult.SorryCount < node.Lean.SorryCount &&
                        SketchValidator.IsStructurallyValid(problem.InitialSketch, composedSketch))
                    {
                        var composedState = BuildState(composedSketch, composedResult, problem.DomainTag);
                        var composedHash  = _encoder.ComputeCanonicalStateHash(composedState);
                        if (!seen.TryGetValue(composedHash, out var existingSorry)
                            || existingSorry > composedResult.SorryCount)
                        {
                            seen[composedHash] = composedResult.SorryCount;
                            stateVisitCount[composedHash] =
                                stateVisitCount.TryGetValue(composedHash, out var cv) ? cv + 1 : 1;
                            acceptedTransitions++;
                            // Priority = sorry count (lower is better); use 0 depth weight
                            // so composed nodes sort ahead of same-sorry-count expansions.
                            frontier.Enqueue(
                                new PlannerNode(composedSketch, composedResult, composedState,
                                    composedHash, node.Depth + 1),
                                priority: composedResult.SorryCount);
                        }
                    }
                }
            }

            var vec = _encoder.Encode(node.State);
            var proposals = await _neo4j.ProposeTacticsFromGoalVectorAsync(
                vec,
                neighborK: config.PlannerNeighborK,
                topK: config.PlannerBranchFactor,
                ct);

            if (proposals.Count == 0) continue;

            var seenSketchesThisNode = new HashSet<string>(StringComparer.Ordinal);

            foreach (var p in proposals)
            {
                if (string.IsNullOrWhiteSpace(p.TacticText)) continue;

                var action = new CanonicalActionCandidate
                {
                    ActionId = p.TacticId,
                    TacticText = p.TacticText,
                    Source = "graph",
                    RankScore = p.RankScore,
                    Similarity = p.NearestGoalSimilarity,
                    HistoricalSuccessRate = p.HistoricalSuccessRate,
                };

                var candidateSketch = SubstituteFirstSorry(node.Sketch, action.TacticText);
                var candidateSketchHash = ComputeHash(candidateSketch);
                if (!seenSketchesThisNode.Add(candidateSketchHash))
                {
                    duplicatePrunes++;
                    continue;
                }

                var candidateResult = await _lean.CompileAsync(candidateSketch, ct);
                if (!candidateResult.Compiled)
                {
                    compileRejects++;
                    continue;
                }

                if (!SketchValidator.IsStructurallyValid(problem.InitialSketch, candidateSketch))
                {
                    structuralRejects++;
                    continue;
                }

                acceptedTransitions++;

                if (candidateResult.SorryCount < bestSorryCount)
                {
                    bestSorryCount = candidateResult.SorryCount;
                    bestSketch = candidateSketch;
                }

                if (candidateResult.IsFullyProved)
                {
                    _log.LogInformation(
                        "Graph planner solved {Id} after {E} expansions and {A} accepted transitions " +
                        "(compileRejects={CR}, structuralRejects={SR}, cyclePrunes={CP}, duplicatePrunes={DP})",
                        problem.Id, expansions, acceptedTransitions, compileRejects, structuralRejects, cyclePrunes, duplicatePrunes);
                    return new PlannerRunResult
                    {
                        Solved = true,
                        FinalSketch = candidateSketch,
                        FinalSorryCount = 0,
                        Expansions = expansions,
                        AcceptedTransitions = acceptedTransitions,
                        FrontierCollapsed = false,
                        CompileRejects = compileRejects,
                        StructuralRejects = structuralRejects,
                        CyclePrunes = cyclePrunes,
                        DuplicatePrunes = duplicatePrunes,
                    };
                }

                var state = BuildState(candidateSketch, candidateResult, problem.DomainTag);
                var hash = _encoder.ComputeCanonicalStateHash(state);

                if (seen.TryGetValue(hash, out var existingSorry) && existingSorry <= candidateResult.SorryCount)
                {
                    duplicatePrunes++;
                    continue;
                }

                seen[hash] = candidateResult.SorryCount;
                stateVisitCount[hash] = stateVisitCount.TryGetValue(hash, out var count) ? count + 1 : 1;
                var expectedCost = ComputeExpectedClosureCost(
                    parentSorryCount: node.Lean.SorryCount,
                    candidateResult,
                    action,
                    depth: node.Depth + 1,
                    config);
                frontier.Enqueue(
                    new PlannerNode(candidateSketch, candidateResult, state, hash, node.Depth + 1),
                    expectedCost);
            }
        }

        var frontierCollapsed = frontier.Count == 0;
        _log.LogInformation(
            "Graph planner finished {Id}: solved={Solved} expansions={E} accepted={A} frontierCollapsed={Collapsed} bestSorry={Best} " +
            "compileRejects={CR} structuralRejects={SR} cyclePrunes={CP} duplicatePrunes={DP}",
            problem.Id, false, expansions, acceptedTransitions, frontierCollapsed, bestSorryCount,
            compileRejects, structuralRejects, cyclePrunes, duplicatePrunes);

        return new PlannerRunResult
        {
            Solved = false,
            FinalSketch = bestSketch,
            FinalSorryCount = bestSorryCount,
            Expansions = expansions,
            AcceptedTransitions = acceptedTransitions,
            FrontierCollapsed = frontierCollapsed,
            CompileRejects = compileRejects,
            StructuralRejects = structuralRejects,
            CyclePrunes = cyclePrunes,
            DuplicatePrunes = duplicatePrunes,
        };
    }

    private static float ComputeExpectedClosureCost(
        int parentSorryCount,
        LeanResult candidateResult,
        CanonicalActionCandidate action,
        int depth,
        OrchestratorConfig config)
    {
        var pendingGoalCount = Math.Max(1, candidateResult.PendingGoalTexts.Length);
        var errorSignal = MathF.Min(1f, candidateResult.Errors.Length / 3f);
        var improved = candidateResult.SorryCount < parentSorryCount ? 1f : 0f;

        return candidateResult.SorryCount
            + depth * config.PlannerDepthWeight
            + (1f - action.RankScore) * config.PlannerRankWeight
            + (1f - action.HistoricalSuccessRate) * config.PlannerSuccessWeight
            + MathF.Log2(1f + pendingGoalCount) * config.PlannerBranchingWeight
            + errorSignal * config.PlannerErrorWeight
            - improved * config.PlannerNoveltyBonus;
    }

    private static ProofState BuildState(string sketch, LeanResult result, string domainTag)
    {
        return new ProofState
        {
            PendingGoals = result.PendingGoalTexts,
            Hypotheses = ExtractHypotheses(sketch),
            TacticHistory = ExtractTactics(sketch),
            SorryCount = result.SorryCount,
            ErrorMessages = result.Errors,
            DomainTag = domainTag,
            SketchHash = ComputeHash(sketch),
        };
    }

    private static string[] ExtractHypotheses(string sketch)
    {
        var lines = sketch.Split('\n');
        var hyps = new List<string>();
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("have ") || trimmed.StartsWith("let "))
            {
                var colon = trimmed.IndexOf(':');
                if (colon > 0) hyps.Add(trimmed[..colon].Trim());
            }
        }
        return [.. hyps];
    }

    private static string[] ExtractTactics(string sketch)
    {
        var tactics = new List<string>();
        foreach (var line in sketch.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("--")) continue;
            var first = trimmed.Split([' ', '\t'], 2)[0];
            if (first.Length is > 0 and < 24) tactics.Add(first);
        }
        return [.. tactics];
    }

    private static string SubstituteFirstSorry(string sketch, string replacement)
    {
        var idx = sketch.IndexOf("sorry", StringComparison.Ordinal);
        if (idx < 0) return sketch;
        return sketch[..idx] + replacement + sketch[(idx + 5)..];
    }

    private static string ComputeHash(string s)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public sealed record PlannerRunResult
{
    public bool Solved { get; init; }
    public required string FinalSketch { get; init; }
    public required int FinalSorryCount { get; init; }
    public required int Expansions { get; init; }
    public required int AcceptedTransitions { get; init; }
    public required bool FrontierCollapsed { get; init; }
    public int CompileRejects { get; init; }
    public int StructuralRejects { get; init; }
    public int CyclePrunes { get; init; }
    public int DuplicatePrunes { get; init; }
}

internal sealed record PlannerNode(
    string Sketch,
    LeanResult Lean,
    ProofState State,
    string CanonicalHash,
    int Depth);
