// ============================================================
// DeepMind Nexus Challenge — Neo4j Schema
// Run this once on your Neo4j instance before first use.
// Compatible with Neo4j 5.x (vector index support required)
// ============================================================

// ── Constraints ────────────────────────────────────────────

CREATE CONSTRAINT proof_fossil_id IF NOT EXISTS
  FOR (f:ProofFossil) REQUIRE f.id IS UNIQUE;

CREATE CONSTRAINT proof_landmark_id IF NOT EXISTS
  FOR (l:ProofLandmark) REQUIRE l.id IS UNIQUE;

CREATE CONSTRAINT math_problem_id IF NOT EXISTS
  FOR (p:MathProblem) REQUIRE p.id IS UNIQUE;

CREATE CONSTRAINT lean_cache_hash IF NOT EXISTS
  FOR (c:LeanCompileCache) REQUIRE c.sketchHash IS UNIQUE;

// ── Vector indexes ──────────────────────────────────────────

// Fossil vault: proven sub-goals indexed for similarity search
CREATE VECTOR INDEX proofFossils IF NOT EXISTS
  FOR (f:ProofFossil) ON (f.stateVector)
  OPTIONS { indexConfig: {
    `vector.dimensions`: 64,
    `vector.similarity_function`: 'cosine'
  }};

// Landmark map: visited proof states indexed for dead-end detection
CREATE VECTOR INDEX proofLandmarks IF NOT EXISTS
  FOR (l:ProofLandmark) ON (l.stateVector)
  OPTIONS { indexConfig: {
    `vector.dimensions`: 64,
    `vector.similarity_function`: 'cosine'
  }};

// ── Regular indexes ─────────────────────────────────────────

CREATE INDEX proof_fossil_domain IF NOT EXISTS
  FOR (f:ProofFossil) ON (f.domainTag);

CREATE INDEX proof_fossil_usecount IF NOT EXISTS
  FOR (f:ProofFossil) ON (f.useCount);

CREATE INDEX proof_landmark_problem IF NOT EXISTS
  FOR (l:ProofLandmark) ON (l.problemId);

CREATE INDEX math_problem_source IF NOT EXISTS
  FOR (p:MathProblem) ON (p.source);   // "OEIS" | "Erdos"

// ── Node property reference ─────────────────────────────────

// :ProofFossil {
//   id: string (uuid),
//   subgoalText: string,
//   tacticBlock: string,
//   stateVector: float[64],
//   domainTag: string,
//   sorryCountBefore: int,
//   sorryCountAfter: int,
//   provedAt: datetime,
//   sourceProblems: string[],
//   useCount: int
// }

// :ProofLandmark {
//   id: string (uuid),
//   problemId: string,
//   stateVector: float[64],
//   sorryCount: int,
//   visitCount: int,
//   deadEndCount: int,
//   bestOutcome: string,   // "Progressed" | "Stalled" | "DeadEnd" | "Solved"
//   firstVisitedAt: datetime
// }

// :MathProblem {
//   id: string,            // e.g. "OEIS_A123456" or "Erdos_125"
//   source: string,
//   leanFilePath: string,
//   status: string,        // "Unsolved" | "Solved" | "InProgress"
//   episodesUsed: int,
//   solvedAt: datetime
// }

// :LeanCompileCache {
//   sketchHash: string,    // SHA256 of sketch text
//   compiled: boolean,
//   remainingGoals: int,
//   sorryCount: int,
//   errors: string[],
//   compileTimeMs: int,
//   cachedAt: datetime
// }

// ── Relationship reference ──────────────────────────────────

// (:ProofFossil)-[:PRECEDES]->(:ProofFossil)
//   {tacticBridge: string}           tactic that connects the two

// (:ProofFossil)-[:PROVED_IN]->(:MathProblem)
//   {episodeId: string, depth: int}

// (:ProofLandmark)-[:TRANSITION]->(:ProofLandmark)
//   {tacticSequence: string, outcome: string, episodeId: string}

// (:ProofLandmark)-[:BELONGS_TO]->(:MathProblem)

// (:MathProblem)-[:USED_FOSSIL]->(:ProofFossil)
//   {episodeId: string, sorryReduced: int}
