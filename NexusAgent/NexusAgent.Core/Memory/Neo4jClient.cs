using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.Driver;
using NexusAgent.Core.Configuration;
using NexusAgent.Core.Models;
using NexusAgent.Core.Planning;

namespace NexusAgent.Core.Memory;

public sealed class Neo4jClient : INeo4jClient, IAsyncDisposable
{
    private readonly IDriver _driver;
    private readonly ILogger<Neo4jClient> _log;
    private readonly string _database;

    public Neo4jClient(IOptions<NexusConfig> config, ILogger<Neo4jClient> log)
    {
        var c = config.Value;
        _driver = GraphDatabase.Driver(c.Neo4jUri, AuthTokens.Basic(c.Neo4jUser, c.Neo4jPassword));
        _database = c.Neo4jDatabase;
        _log = log;
    }

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        await using var session = _driver.AsyncSession(o => o.WithDatabase(_database));
        await session.ExecuteWriteAsync(async tx =>
        {
            foreach (var stmt in SchemaStatements)
                await tx.RunAsync(stmt);
            return 0;
        });
        _log.LogInformation("Neo4j schema ensured");
    }

    // ---- Fossil vault ----

    public async Task UpsertFossilAsync(ProofFossil fossil, CancellationToken ct)
    {
        await using var session = _driver.AsyncSession(o => o.WithDatabase(_database));
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                """
                MERGE (f:ProofFossil {id: $id})
                SET f.subgoalText = $subgoalText,
                    f.tacticBlock = $tacticBlock,
                    f.stateVector = $stateVector,
                    f.domainTag = $domainTag,
                    f.sorryCountBefore = $sorryCountBefore,
                    f.sorryCountAfter = $sorryCountAfter,
                    f.provedAt = datetime($provedAt),
                    f.sourceProblems = $sourceProblems,
                    f.compilationVerified = coalesce(f.compilationVerified, $compilationVerified),
                    f.runId = coalesce(f.runId, $runId),
                    f.useCount = coalesce(f.useCount, 0)
                """,
                new
                {
                    id = fossil.Id,
                    subgoalText = fossil.SubgoalText,
                    tacticBlock = fossil.TacticBlock,
                    stateVector = fossil.StateVector.Select(f => (double)f).ToArray(),
                    domainTag = fossil.DomainTag,
                    sorryCountBefore = fossil.SorryCountBefore,
                    sorryCountAfter = fossil.SorryCountAfter,
                    provedAt = fossil.ProvedAt.ToString("o"),
                    sourceProblems = fossil.SourceProblems,
                    compilationVerified = fossil.CompilationVerified,
                    runId = fossil.RunId ?? "",
                });
            return 0;
        });
    }

    public async Task<IReadOnlyList<FossilMatch>> NearestFossilsAsync(
        float[] queryVector, int topK, float minSimilarity, CancellationToken ct)
    {
        await using var session = _driver.AsyncSession(o => o.WithDatabase(_database));
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                """
                CALL db.index.vector.queryNodes('proofFossils', $topK, $vec)
                YIELD node, score
                WHERE score >= $minScore
                  AND NOT coalesce(node.quarantined, false)
                RETURN node.id              AS id,
                       node.subgoalText     AS subgoalText,
                       node.tacticBlock     AS tacticBlock,
                       node.stateVector     AS stateVector,
                       node.domainTag       AS domainTag,
                       node.sorryCountBefore AS sorryCountBefore,
                       node.sorryCountAfter  AS sorryCountAfter,
                       toString(node.provedAt) AS provedAt,
                       node.sourceProblems  AS sourceProblems,
                      coalesce(node.compilationVerified, false) AS compilationVerified,
                       coalesce(node.useCount, 0) AS useCount,
                       score                AS similarity
                """,
                new
                {
                    topK,
                    vec = queryVector.Select(f => (double)f).ToArray(),
                    minScore = (double)minSimilarity,
                });

            var results = new List<FossilMatch>();
            await foreach (var rec in cursor)
            {
                var fossil = new ProofFossil
                {
                    Id = rec["id"].As<string>(),
                    SubgoalText = rec["subgoalText"].As<string>(),
                    TacticBlock = rec["tacticBlock"].As<string>(),
                    StateVector = rec["stateVector"].As<List<object>>()
                        .Select(x => Convert.ToSingle(x)).ToArray(),
                    DomainTag = rec["domainTag"].As<string>(),
                    SorryCountBefore = rec["sorryCountBefore"].As<int>(),
                    SorryCountAfter = rec["sorryCountAfter"].As<int>(),
                    ProvedAt = DateTime.Parse(rec["provedAt"].As<string>()),
                    SourceProblems = rec["sourceProblems"].As<List<object>>()
                        .Select(x => x.ToString() ?? "").ToArray(),
                    CompilationVerified = rec["compilationVerified"].As<bool>(),
                    UseCount = rec["useCount"].As<int>(),
                };
                results.Add(new FossilMatch(fossil, (float)rec["similarity"].As<double>()));
            }
            return results;
        });
    }

    public async Task<IReadOnlyList<GraphTacticProposal>> ProposeTacticsFromGoalVectorAsync(
        float[] queryVector, int neighborK, int topK, CancellationToken ct)
    {
        await using var session = _driver.AsyncSession(o => o.WithDatabase(_database));
        try
        {
            return await session.ExecuteReadAsync(async tx =>
            {
                var cursor = await tx.RunAsync(
                    """
                    CALL db.index.vector.queryNodes('goalshape_state_vec_idx', $neighborK, $vec)
                    YIELD node, score
                    MATCH (node)-[r:APPLIES]->()
                    MATCH (t:Tactic {id: r.tactic_id})
                    WITH t.id AS tacticId,
                         t.text AS tacticText,
                         max(score) AS nearestGoalSimilarity,
                         sum(coalesce(r.success_sum, 0.0)) AS succ,
                         sum(coalesce(r.count, 0.0)) AS n
                    WHERE n > 0
                    RETURN tacticId,
                           tacticText,
                           nearestGoalSimilarity,
                           (succ / n) AS historicalSuccessRate,
                           toInteger(n) AS supportCount
                    ORDER BY nearestGoalSimilarity DESC, supportCount DESC
                    LIMIT $topK
                    """,
                    new
                    {
                        neighborK,
                        topK,
                        vec = queryVector.Select(f => (double)f).ToArray(),
                    });

                var results = new List<GraphTacticProposal>();
                await foreach (var rec in cursor)
                {
                    var nearest = (float)rec["nearestGoalSimilarity"].As<double>();
                    var success = (float)rec["historicalSuccessRate"].As<double>();
                    var support = rec["supportCount"].As<int>();

                    // Conservative rank mixing retrieval confidence and empirical tactic reliability.
                    var rank = 0.65f * nearest
                        + 0.25f * success
                        + 0.10f * MathF.Min(1f, MathF.Log10(Math.Max(1f, support) + 1f));

                    results.Add(new GraphTacticProposal
                    {
                        TacticId = rec["tacticId"].As<string>(),
                        TacticText = rec["tacticText"].As<string>(),
                        NearestGoalSimilarity = nearest,
                        HistoricalSuccessRate = success,
                        SupportCount = support,
                        RankScore = rank,
                    });
                }

                return (IReadOnlyList<GraphTacticProposal>)results
                    .OrderByDescending(x => x.RankScore)
                    .Take(topK)
                    .ToArray();
            });
        }
        catch (ClientException ex)
        {
            _log.LogDebug(
                ex,
                "Graph-native proposer unavailable (missing GoalShape graph/vector index): {Message}",
                ex.Message);
            return Array.Empty<GraphTacticProposal>();
        }
    }

    public async Task IncrementFossilUseCountAsync(string fossilId, string currentRunId, CancellationToken ct)
    {
        await using var session = _driver.AsyncSession(o => o.WithDatabase(_database));
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                """
                MATCH (f:ProofFossil {id: $id})
                SET f.useCount = coalesce(f.useCount, 0) + 1,
                    f.crossRun = CASE
                        WHEN f.runId IS NULL OR f.runId <> $runId THEN true
                        ELSE coalesce(f.crossRun, false)
                    END
                """,
                new { id = fossilId, runId = currentRunId });
            return 0;
        });
    }

    public async Task<int> CountFossilsAsync(CancellationToken ct)
    {
        await using var session = _driver.AsyncSession(o => o.WithDatabase(_database));
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("MATCH (f:ProofFossil) RETURN count(f) AS n");
            var rec = await cursor.SingleAsync();
            return rec["n"].As<int>();
        });
    }

    public async Task<FossilAnalysis> FossilAnalysisAsync(CancellationToken ct)
    {
        await using var session = _driver.AsyncSession(o => o.WithDatabase(_database));

        // --- Total counts ---
        int totalFossils = 0, totalLandmarks = 0, solvedProblems = 0, crossRunHits = 0;
        var topFossils = new List<FossilSummary>();
        var domainDist = new Dictionary<string, int>();
        var chains = new Dictionary<string, int>();

        await session.ExecuteReadAsync(async tx =>
        {
            // Counts
            var countCursor = await tx.RunAsync(
                """
                MATCH (f:ProofFossil) WITH count(f) AS fossils,
                      sum(CASE WHEN f.crossRun = true THEN 1 ELSE 0 END) AS crossRun
                OPTIONAL MATCH (l:ProofLandmark) WITH fossils, crossRun, count(l) AS landmarks
                OPTIONAL MATCH (p:MathProblem {status: 'Solved'}) 
                RETURN fossils, landmarks, crossRun, count(p) AS solved
                """);
            var countRec = await countCursor.SingleAsync();
            totalFossils = countRec["fossils"].As<int>();
            totalLandmarks = countRec["landmarks"].As<int>();
            solvedProblems = countRec["solved"].As<int>();
            crossRunHits = countRec["crossRun"].As<int>();

            // Top fossils by use count
            var topCursor = await tx.RunAsync(
                """
                MATCH (f:ProofFossil)
                RETURN f.id              AS id,
                       f.domainTag       AS domain,
                       coalesce(f.useCount, 0)         AS uses,
                       (f.sorryCountBefore - f.sorryCountAfter) AS reduction,
                       left(f.subgoalText, 120)        AS snippet,
                       left(f.tacticBlock, 80)         AS tactic,
                       f.sourceProblems  AS sources
                ORDER BY uses DESC, reduction DESC
                LIMIT 10
                """);
            await foreach (var rec in topCursor)
            {
                topFossils.Add(new FossilSummary(
                    Id:             rec["id"].As<string>(),
                    DomainTag:      rec["domain"].As<string>(),
                    UseCount:       rec["uses"].As<int>(),
                    SorryReduction: rec["reduction"].As<int>(),
                    SubgoalSnippet: rec["snippet"].As<string>(),
                    TacticSnippet:  rec["tactic"].As<string>(),
                    SourceProblems: rec["sources"].As<List<object>>()
                                        .Select(x => x.ToString() ?? "").ToArray()));
            }

            // Domain distribution
            var domCursor = await tx.RunAsync(
                """
                MATCH (f:ProofFossil)
                RETURN f.domainTag AS domain, count(f) AS n
                ORDER BY n DESC
                """);
            await foreach (var rec in domCursor)
                domainDist[rec["domain"].As<string>()] = rec["n"].As<int>();

            // PRECEDES chain depths (walk up to depth 10)
            var chainCursor = await tx.RunAsync(
                """
                MATCH path = (root:ProofFossil)-[:PRECEDES*1..10]->(leaf:ProofFossil)
                WHERE NOT ()-[:PRECEDES]->(root)
                WITH root.id AS rootId, max(length(path)) AS depth
                ORDER BY depth DESC
                LIMIT 5
                RETURN rootId, depth
                """);
            await foreach (var rec in chainCursor)
                chains[rec["rootId"].As<string>()] = rec["depth"].As<int>();

            return 0;
        });

        return new FossilAnalysis
        {
            TotalFossils        = totalFossils,
            TotalLandmarks      = totalLandmarks,
            SolvedProblems      = solvedProblems,
            CrossRunHits        = crossRunHits,
            TopFossils          = topFossils,
            DomainDistribution  = domainDist,
            DeepestPrecedesChains = chains,
        };
    }

    // ---- Landmark graph ----

    public async Task<ProofLandmark> UpsertLandmarkAsync(ProofLandmark landmark, CancellationToken ct)
    {
        await using var session = _driver.AsyncSession(o => o.WithDatabase(_database));
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                """
                MERGE (l:ProofLandmark {id: $id})
                ON CREATE SET l.stateVector = $vec,
                              l.sorryCount = $sorry,
                              l.visitCount = 1,
                              l.deadEndCount = 0,
                              l.bestOutcome = $outcome,
                              l.problemId = $problemId,
                              l.firstVisitedAt = datetime()
                ON MATCH  SET l.visitCount = l.visitCount + 1,
                              l.bestOutcome = CASE
                                  WHEN $outcome = 'Solved' THEN 'Solved'
                                  WHEN l.bestOutcome = 'Solved' THEN 'Solved'
                                  ELSE $outcome END
                """,
                new
                {
                    id = landmark.Id,
                    vec = landmark.StateVector.Select(f => (double)f).ToArray(),
                    sorry = landmark.SorryCount,
                    outcome = landmark.BestOutcome.ToString(),
                    problemId = landmark.ProblemId,
                });
            return 0;
        });
        return landmark;
    }

    public async Task RecordTransitionAsync(
        string fromLandmarkId, string toLandmarkId,
        string tacticSequence, TransitionOutcome outcome,
        string episodeId, CancellationToken ct)
    {
        await using var session = _driver.AsyncSession(o => o.WithDatabase(_database));
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                """
                MATCH (a:ProofLandmark {id: $from})
                MATCH (b:ProofLandmark {id: $to})
                MERGE (a)-[t:TRANSITION {tacticSequence: $tactic, episodeId: $episode}]->(b)
                SET t.outcome = $outcome
                """,
                new { from = fromLandmarkId, to = toLandmarkId, tactic = tacticSequence,
                      outcome = outcome.ToString(), episode = episodeId });

            if (outcome == TransitionOutcome.DeadEnd)
            {
                await tx.RunAsync(
                    "MATCH (b:ProofLandmark {id: $to}) SET b.deadEndCount = coalesce(b.deadEndCount, 0) + 1",
                    new { to = toLandmarkId });
            }
            return 0;
        });
    }

    public async Task<IReadOnlyList<ProofLandmark>> NearbyLandmarksAsync(
        float[] queryVector, int topK, CancellationToken ct)
    {
        await using var session = _driver.AsyncSession(o => o.WithDatabase(_database));
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                """
                CALL db.index.vector.queryNodes('proofLandmarks', $topK, $vec)
                YIELD node, score
                RETURN node.id           AS id,
                       node.stateVector  AS vec,
                       node.sorryCount   AS sorry,
                       node.visitCount   AS visits,
                       node.deadEndCount AS deads,
                       node.bestOutcome  AS outcome,
                       node.problemId    AS problemId,
                       score             AS similarity
                """,
                new { topK, vec = queryVector.Select(f => (double)f).ToArray() });

            var results = new List<ProofLandmark>();
            await foreach (var rec in cursor)
            {
                results.Add(new ProofLandmark
                {
                    Id = rec["id"].As<string>(),
                    StateVector = rec["vec"].As<List<object>>().Select(x => Convert.ToSingle(x)).ToArray(),
                    SorryCount = rec["sorry"].As<int>(),
                    VisitCount = rec["visits"].As<int>(),
                    DeadEndCount = rec["deads"].As<int>(),
                    BestOutcome = Enum.Parse<TransitionOutcome>(rec["outcome"].As<string>()),
                    ProblemId = rec["problemId"].As<string>(),
                });
            }
            return results;
        });
    }

    public async Task<IReadOnlyList<ProofLandmark>> NearbySolvedLandmarksAsync(
        float[] queryVector, int topK, CancellationToken ct)
    {
        await using var session = _driver.AsyncSession(o => o.WithDatabase(_database));
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                """
                CALL db.index.vector.queryNodes('proofLandmarks', $topK, $vec)
                YIELD node, score
                WHERE node.bestOutcome = 'Solved'
                RETURN node.id           AS id,
                       node.stateVector  AS vec,
                       node.sorryCount   AS sorry,
                       node.visitCount   AS visits,
                       node.deadEndCount AS deads,
                       node.bestOutcome  AS outcome,
                       node.problemId    AS problemId,
                       score             AS similarity
                """,
                new { topK, vec = queryVector.Select(f => (double)f).ToArray() });

            var results = new List<ProofLandmark>();
            await foreach (var rec in cursor)
            {
                results.Add(new ProofLandmark
                {
                    Id = rec["id"].As<string>(),
                    StateVector = rec["vec"].As<List<object>>().Select(x => Convert.ToSingle(x)).ToArray(),
                    SorryCount = rec["sorry"].As<int>(),
                    VisitCount = rec["visits"].As<int>(),
                    DeadEndCount = rec["deads"].As<int>(),
                    BestOutcome = Enum.Parse<TransitionOutcome>(rec["outcome"].As<string>()),
                    ProblemId = rec["problemId"].As<string>(),
                });
            }
            return results;
        });
    }

    public async Task<IReadOnlyList<string>?> ShortestSuccessfulPathAsync(
        string fromLandmarkId, string toLandmarkId, CancellationToken ct)
    {
        await using var session = _driver.AsyncSession(o => o.WithDatabase(_database));
        return await session.ExecuteReadAsync(async tx =>
        {
            // shortestPath finds the BFS-shortest path; WHERE ALL(...) then filters
            // to only accept paths whose every edge has a non-failing outcome.
            // Bounds are literals (Neo4j does not support parameterised range bounds).
            var cursor = await tx.RunAsync(
                """
                MATCH path = shortestPath(
                  (a:ProofLandmark {id: $from})-[:TRANSITION*1..10]->(b:ProofLandmark {id: $to})
                )
                WHERE ALL(r IN relationships(path) WHERE r.outcome IN ['Progressed', 'Solved'])
                RETURN [r IN relationships(path) | r.tacticSequence] AS tactics
                LIMIT 1
                """,
                new { from = fromLandmarkId, to = toLandmarkId });

            IReadOnlyList<string>? result = null;
            await foreach (var rec in cursor)
            {
                result = rec["tactics"].As<List<object>>()
                    .Select(x => x?.ToString() ?? string.Empty)
                    .ToArray();
            }
            return result;
        });
    }

    // ---- Compile cache ----

    public async Task<LeanResult?> GetCompileCacheAsync(string sketchHash, CancellationToken ct)
    {
        await using var session = _driver.AsyncSession(o => o.WithDatabase(_database));
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                """
                MATCH (c:LeanCompileCache {sketchHash: $h})
                RETURN c.compiled        AS compiled,
                       c.remainingGoals  AS goals,
                       c.sorryCount      AS sorries,
                       c.errors          AS errors,
                       c.compileTimeMs   AS ms
                """,
                new { h = sketchHash });
            try
            {
                var rec = await cursor.SingleAsync();
                return new LeanResult
                {
                    Compiled = rec["compiled"].As<bool>(),
                    RemainingGoals = rec["goals"].As<int>(),
                    SorryCount = rec["sorries"].As<int>(),
                    Errors = rec["errors"].As<List<object>>()
                        .Select(x => x.ToString() ?? "").ToArray(),
                    Warnings = [],
                    CompileTime = TimeSpan.FromMilliseconds(rec["ms"].As<int>()),
                    PendingGoalTexts = [],
                };
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        });
    }

    public async Task PutCompileCacheAsync(string sketchHash, LeanResult result, CancellationToken ct)
    {
        await using var session = _driver.AsyncSession(o => o.WithDatabase(_database));
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                """
                MERGE (c:LeanCompileCache {sketchHash: $h})
                SET c.compiled       = $compiled,
                    c.remainingGoals = $goals,
                    c.sorryCount     = $sorries,
                    c.errors         = $errors,
                    c.compileTimeMs  = $ms,
                    c.cachedAt       = datetime()
                """,
                new
                {
                    h = sketchHash,
                    compiled = result.Compiled,
                    goals = result.RemainingGoals,
                    sorries = result.SorryCount,
                    errors = result.Errors,
                    ms = (int)result.CompileTime.TotalMilliseconds,
                });
            return 0;
        });
    }

    // ---- Problem registry ----

    public async Task UpsertProblemAsync(string id, string source, string leanFilePath, CancellationToken ct)
    {
        await using var session = _driver.AsyncSession(o => o.WithDatabase(_database));
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                """
                MERGE (p:MathProblem {id: $id})
                ON CREATE SET p.source = $source, p.leanFilePath = $path,
                              p.status = 'InProgress', p.episodesUsed = 0
                """,
                new { id, source, path = leanFilePath });
            return 0;
        });
    }

    public async Task MarkProblemSolvedAsync(string id, int episodesUsed, CancellationToken ct)
    {
        await using var session = _driver.AsyncSession(o => o.WithDatabase(_database));
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                """
                MATCH (p:MathProblem {id: $id})
                SET p.status = 'Solved',
                    p.episodesUsed = $episodes,
                    p.solvedAt = datetime()
                """,
                new { id, episodes = episodesUsed });
            return 0;
        });
    }

    public async Task<bool> IsProblemSolvedAsync(string id, CancellationToken ct)
    {
        await using var session = _driver.AsyncSession(o => o.WithDatabase(_database));
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (p:MathProblem {id: $id, status: 'Solved'}) RETURN count(p) AS n",
                new { id });
            var record = await cursor.SingleAsync();
            return record["n"].As<int>() > 0;
        });
    }

    public async ValueTask DisposeAsync() => await _driver.DisposeAsync();

    // ---- ErdosHypergraph edge store ----------------------------------------

    public async Task UpsertHyperedgesAsync(IEnumerable<HyperedgeRecord> edges, CancellationToken ct)
    {
        // Batch into chunks of 200 to avoid huge parameter payloads.
        const int batchSize = 200;
        var batch = new List<HyperedgeRecord>(batchSize);
        foreach (var edge in edges)
        {
            batch.Add(edge);
            if (batch.Count < batchSize) continue;
            await FlushBatchAsync(batch, ct);
            batch.Clear();
        }
        if (batch.Count > 0) await FlushBatchAsync(batch, ct);

        async Task FlushBatchAsync(List<HyperedgeRecord> b, CancellationToken token)
        {
            await using var session = _driver.AsyncSession(o => o.WithDatabase(_database));
            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync(
                    """
                    UNWIND $rows AS row
                    MERGE (e:HyperedgeRecord {id: row.id})
                    SET e.lemmaName       = row.lemmaName,
                        e.functionDisplay = row.lemmaName,
                        e.inputs          = row.inputs,
                        e.output          = row.output,
                        e.outputHash      = row.outputHash,
                        e.inputCount      = row.inputCount,
                        e.builtAt         = datetime(row.builtAt),
                        e.seedRun         = row.seedRun
                    """,
                    new
                    {
                        rows = b.Select(e => new
                        {
                            id          = e.Id,
                            lemmaName   = e.LemmaName,
                            inputs      = e.Inputs,
                            output      = e.Output,
                            outputHash  = (long)(e.OutputHash),   // Neo4j has no UInt64; cast to signed
                            inputCount  = e.Inputs.Length,
                            builtAt     = e.BuiltAt.ToString("o"),
                            seedRun     = e.SeedRun,
                        }).ToArray()
                    });
                return 0;
            });
        }
    }

    public async Task<IReadOnlyList<HyperedgeRecord>> GetAllHyperedgesAsync(CancellationToken ct)
    {
        await using var session = _driver.AsyncSession(o => o.WithDatabase(_database));
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                """
                MATCH (e:HyperedgeRecord)
                RETURN e.id          AS id,
                       e.lemmaName   AS lemmaName,
                       e.inputs      AS inputs,
                       e.output      AS output,
                       e.outputHash  AS outputHash,
                       toString(e.builtAt) AS builtAt,
                       e.seedRun     AS seedRun
                """);

            var results = new List<HyperedgeRecord>();
            await foreach (var rec in cursor)
            {
                results.Add(new HyperedgeRecord
                {
                    Id         = rec["id"].As<string>(),
                    LemmaName  = rec["lemmaName"].As<string>(),
                    Inputs     = rec["inputs"].As<List<object>>()
                                     .Select(x => x.ToString() ?? "").ToArray(),
                    Output     = rec["output"].As<string>(),
                    OutputHash = (ulong)rec["outputHash"].As<long>(),
                    BuiltAt    = DateTime.Parse(rec["builtAt"].As<string>()),
                    SeedRun    = rec["seedRun"].As<string>(),
                });
            }
            return results;
        });
    }

    private static readonly string[] SchemaStatements =
    [
        "CREATE CONSTRAINT proof_fossil_id IF NOT EXISTS FOR (f:ProofFossil) REQUIRE f.id IS UNIQUE",
        "CREATE CONSTRAINT proof_landmark_id IF NOT EXISTS FOR (l:ProofLandmark) REQUIRE l.id IS UNIQUE",
        "CREATE CONSTRAINT math_problem_id IF NOT EXISTS FOR (p:MathProblem) REQUIRE p.id IS UNIQUE",
        "CREATE CONSTRAINT lean_cache_hash IF NOT EXISTS FOR (c:LeanCompileCache) REQUIRE c.sketchHash IS UNIQUE",
        """
        CREATE VECTOR INDEX proofFossils IF NOT EXISTS
        FOR (f:ProofFossil) ON (f.stateVector)
        OPTIONS { indexConfig: {
            `vector.dimensions`: 64,
            `vector.similarity_function`: 'cosine'
        }}
        """,
        """
        CREATE VECTOR INDEX proofLandmarks IF NOT EXISTS
        FOR (l:ProofLandmark) ON (l.stateVector)
        OPTIONS { indexConfig: {
            `vector.dimensions`: 64,
            `vector.similarity_function`: 'cosine'
        }}
        """,
        "CREATE INDEX proof_fossil_domain IF NOT EXISTS FOR (f:ProofFossil) ON (f.domainTag)",
        "CREATE CONSTRAINT hg_edge_id IF NOT EXISTS FOR (e:HyperedgeRecord) REQUIRE e.id IS UNIQUE",
        "CREATE INDEX hg_edge_output IF NOT EXISTS FOR (e:HyperedgeRecord) ON (e.outputHash)",
    ];
}
