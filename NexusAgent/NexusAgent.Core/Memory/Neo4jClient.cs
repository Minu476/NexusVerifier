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
                RETURN node.id              AS id,
                       node.subgoalText     AS subgoalText,
                       node.tacticBlock     AS tacticBlock,
                       node.stateVector     AS stateVector,
                       node.domainTag       AS domainTag,
                       node.sorryCountBefore AS sorryCountBefore,
                       node.sorryCountAfter  AS sorryCountAfter,
                       toString(node.provedAt) AS provedAt,
                       node.sourceProblems  AS sourceProblems,
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
                    UseCount = rec["useCount"].As<int>(),
                };
                results.Add(new FossilMatch(fossil, (float)rec["similarity"].As<double>()));
            }
            return results;
        });
    }

    public async Task IncrementFossilUseCountAsync(string fossilId, CancellationToken ct)
    {
        await using var session = _driver.AsyncSession(o => o.WithDatabase(_database));
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                "MATCH (f:ProofFossil {id: $id}) SET f.useCount = coalesce(f.useCount, 0) + 1",
                new { id = fossilId });
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

    public async ValueTask DisposeAsync() => await _driver.DisposeAsync();

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
    ];
}
