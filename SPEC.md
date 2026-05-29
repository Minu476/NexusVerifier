# NexusVerifier
### Rich Learning V2 Graph-Augmented Formal Proof Search

**Owner:** Nasser Towfigh — Rich Learning Inc., West Vancouver BC  
**Reference paper:** *Advancing Mathematics Research with AI-Driven Formal Proof Search*  
arXiv:2605.22763v1, Google DeepMind, May 21 2026  
**Local LLM:** Qwen3.6-35B-A3B local (Tier 1) + DeepSeek V4 API (Tier 2/3)  
**Graph backend:** Neo4j (Mac Mini M4 Pro, 48 GB RAM, 8 TB SSD)  
**Lean version:** Lean 4 + Mathlib (installed via `elan`)  
**Language:** C# / .NET 10, referencing `RichLearning.V2` from Rich-Learning-Base

---

## 1. Project Goal

Build a graph-augmented formal proof search agent that replicates and improves upon
AlphaProof Nexus's approach using the Rich Learning V2 architecture. The central
hypothesis is:

> **A proof search agent guided by Rich Learning's topological graph — fossil vault
> for sub-goal reuse, Cartographer for structural navigation, ConsonanceChecker for
> hallucination filtering — will solve the same classes of problems at a fraction of
> the LLM cost, and accumulate a persistent knowledge graph that improves across
> problems.**

Success is measured against two DeepMind benchmarks:
- OEIS conjectures: DeepMind solved 44/492. We target ≥10 on a local subset using
  Qwen3.6-35B-A3B local (free) + DeepSeek V4 API (cheap, with prefix caching).
- Erdős problems (stretch): DeepMind solved 9/353. Any solve here is a publication-
  grade result.

Secondary goal: **empirically validate the DAPSA provable-correctness claim** in a
domain where ground truth is binary (Lean either compiles or it doesn't). This is the
cleanest possible test bed for Rich Learning's core thesis.

---

## 2. What DeepMind Did (and Where They Left Value on the Table)

### Their architecture (4 agents, A→D)

```
Agent A (Basic):
  N independent subagents, no shared state
  Each runs a "Ralph loop": LLM generate → Lean verify → error feed back → repeat
  Terminates at 3000 episodes per problem

Agent B = A + AlphaProof (RL-based Lean sub-prover)

Agent C = A + Evolutionary population database
  Sketches rated by Elo via LLM critics (Gemini Flash)
  P-UCB sampling to drive search

Agent D (Full-featured) = B + C
  Used for all benchmark runs
  Cost: ~$100–300 per problem (Gemini 3.1 Pro cloud)
```

### Their explicit failure modes (from the paper)

**Failure 1 — Sorry offloading:** The agent frequently rewrote the hard part of a
proof into a helper lemma marked `sorry`, repeating the original difficulty in
disguised form. Prompting against this failed.

**Failure 2 — Hallucinated lemmas:** Top-Elo sketches contained `sorry` lemmas
described as "established results in the literature" that proved to be fabrications.
The Lean compiler cannot detect these — it just accepts `sorry`.

**Failure 3 — No cross-problem learning:** Each problem run starts cold. CRT tactics
proved in problem #12(i) are not available to problem #125. All discovered proof
patterns are discarded after each run.

**Failure 4 — Binary fitness / probabilistic search mismatch:** Elo is a continuous
approximation of a binary outcome (compiles or doesn't). It introduces noise and
requires expensive rating agents just to bridge the gap.

---

## 3. Architecture: Rich Learning Nexus Agent

### 3.1 Dependency chain

```
DeepMind_Nexus_Challenge
    │
    ├── NexusAgent.csproj
    │       └── ProjectReference → Rich-Learning-Base/src/RichLearning.V2
    │
    ├── Components
    │       ├── LeanOracle              — calls local Lean compiler, parses output
    │       ├── ProofStateEncoder       — NEW: replaces DefaultStateEncoder
    │       ├── ProofFossilizer         — wraps FsdeFossilizer for proof sub-goals
    │       ├── HallucinationGate       — wraps IConsonanceChecker for sorry lemmas
    │       ├── ProofCartographer       — wraps Cartographer for proof-path topology
    │       ├── NexusProverSubagent     — single episode loop (Ralph loop equivalent)
    │       └── NexusOrchestrator       — runs episodes, routes fossil vs. LLM
    │
    └── Benchmarks
            ├── OeisBenchmark.cs        — 492 OEIS conjectures (start here)
            └── ErdosBenchmark.cs       — 353 Erdős problems (stretch target)
```

### 3.2 Data flow

```
Problem (Lean file with sorry)
        │
        ▼
┌─────────────────────┐
│  ProofStateEncoder  │  Encodes current proof state into vector
│  (dim 64, see §4.1) │  (pending goals, tactic history, sorry count)
└─────────────────────┘
        │
        ▼
┌─────────────────────────────────────────────────────────────────┐
│  DapsaEngine<ProofRequest, LeanSketch>                          │
│                                                                 │
│  PERCEIVE  → encode proof state                                 │
│  QUERY S^P → fossil vault: has this sub-goal been proved?      │
│              if yes → substitute fossil proof → verify → done  │
│  CONSONANCE CHECK → hallucination gate on sorry lemmas          │
│  QUERY S^A → TieredLlmRouter selects Qwen/Flash/Pro and generates next tactic  │
│  LEARN     → LeanOracle compiles → binary feedback             │
│  FOSSILIZE → if subgoal proved: write to Neo4j fossil vault    │
└─────────────────────────────────────────────────────────────────┘
        │
        ▼
┌─────────────────────┐
│  ProofCartographer  │  Maintains landmark graph of proof paths
│                     │  Detects dead-end topology, guides next episode
└─────────────────────┘
        │
        ▼
    LeanOracle
    (final full-proof compilation — binary pass/fail)
```

---

## 4. Component Specifications

### 4.1 ProofStateEncoder

**Purpose:** Encode a Lean proof state into a fixed-dimensional vector for fossil
matching. Replaces the FSDE `DefaultStateEncoder` (dim 8, code-analysis-specific).

**Input:** A `ProofState` record containing:
```csharp
record ProofState(
    string[] PendingGoals,      // Lean goal strings after current tactic
    string[] Hypotheses,        // Available hypotheses in scope
    string[] TacticHistory,     // Sequence of tactics applied so far
    int SorryCount,             // Number of remaining sorry placeholders
    string[] ErrorMessages,     // Lean compiler errors from last attempt
    string DomainTag            // "combinatorics" | "algebra" | "analysis" | etc.
);
```

**Output:** `float[]` of dimension 64.

**Encoding strategy:**
- Slot 0–15: TF-IDF bag-of-tactics over the 200 most common Lean 4 / Mathlib tactics
  (learned from Mathlib source). Captures structural similarity of proof approach.
- Slot 16–31: Semantic embedding of pending goals (average of goal token hashes,
  modular arithmetic). Goal shape matters more than exact wording.
- Slot 32–47: Hypothesis fingerprint (sorted hypothesis name hashes, top-8). Two
  states with the same available lemmas are likely similar.
- Slot 48–55: Tactic history n-gram (last 4 tactics as 2-grams, hashed).
- Slot 56–59: Scalar features: sorry_count, error_count, goal_depth, hypothesis_count.
- Slot 60–63: One-hot domain tag (combinatorics, algebra, analysis, other).

**Normalization:** L2-normalize the full vector. Cosine similarity in fossil matching
is then equivalent to dot product.

**Key invariant:** Two proof states encoding the same mathematical sub-goal (e.g.,
"prove that ∀ n, n divides n*(n+1)/2") must produce similar vectors even if the
surrounding context differs. The goal-slot (16–31) carries the most weight for this.

### 4.2 LeanOracle

**Purpose:** The ground-truth binary judge. Wraps the local Lean 4 compiler.

```csharp
interface ILeanOracle
{
    Task<LeanResult> CompileAsync(string leanSketch, CancellationToken ct);
    Task<LeanResult> CheckSubgoalAsync(string goalStatement, CancellationToken ct);
}

record LeanResult(
    bool Compiled,              // true = no sorry, no errors
    int RemainingGoals,         // 0 = fully proved
    int SorryCount,             // still-open sorry placeholders
    string[] Errors,            // Lean error messages if failed
    string[] Warnings,
    TimeSpan CompileTime
);
```

**Implementation:** Shell out to `lake build` or `lean --run` via `Process`. Parse
stdout/stderr for goal state. Lean's error messages are structured enough for regex
extraction of goal counts and error locations.

**Timeout:** 30 seconds per compilation attempt. Lean can hang on infinite loops in
tactic search — always cancel and report timeout as failure.

**Caching:** Cache `(sketch hash → LeanResult)` in Neo4j. Identical sketch strings
never recompile. This alone eliminates a large fraction of duplicate work.

### 4.3 ProofFossilizer

**Purpose:** Persist proven sub-goals to the Rich Learning fossil vault, making them
retrievable across future problems.

**What gets fossilized:**
- Any Lean sub-goal that the LeanOracle compiled without `sorry`, regardless of
  whether the parent proof is complete.
- The complete tactic block that proved it.
- The `ProofStateVector` at the time of fossilization.
- Metadata: problem origin, domain tag, proof depth, compile time.

**Fossil schema (Neo4j node):**
```
(:ProofFossil {
    id: uuid,
    subgoalText: string,        // the Lean goal statement
    tacticBlock: string,        // the proof tactics
    stateVector: float[64],     // for nearest-neighbor matching
    domainTag: string,
    sorryCountBefore: int,      // how many sorry remained before this fossil
    sorryCountAfter: int,       // should be 0 for a clean fossil
    provedAt: datetime,
    sourceProblems: string[],   // which problems produced this fossil
    useCount: int               // how many times reused
})
```

**Fossil edges (Neo4j relationships):**
```
(:ProofFossil)-[:PRECEDES]->(:ProofFossil)   // tactic A leads to tactic B
(:ProofFossil)-[:SHARED_HYPOTHESIS]->(:Lemma) // both use the same Mathlib lemma
(:ProofFossil)-[:SAME_DOMAIN]->(:Domain)
```

**Retrieval:** On a new proof state, encode to vector, query Neo4j for top-5 nearest
fossils by cosine similarity (index: `VECTOR INDEX proofFossils ON stateVector`).
Return the `tacticBlock` of the best match above the consonance threshold.

### 4.4 HallucinationGate

**Purpose:** Filter helper lemmas that contain `sorry` but are described as "known
results." This is DeepMind's Failure Mode 2 — directly addressed.

**Mechanism:** Wraps `IConsonanceChecker<ProofRequest>`.

**Check logic:**
1. Scan the current sketch for sorry-containing helper lemmas.
2. For each such lemma, extract the statement text.
3. Query the fossil vault: does any fossil match this statement with high similarity?
   - **Match found above threshold:** The lemma is plausible (we may have proved it
     before). Allow it, flag for priority verification.
   - **No match, but statement contains known Mathlib theorem names:** Query Mathlib
     name index. If found, replace `sorry` with the Mathlib reference automatically.
   - **No match, statement references obscure "well-known result":** Mark as suspected
     hallucination. Feed a warning into the LLM prompt on the next episode:
     *"Helper lemma [X] was flagged as unverified. Either prove it inline or cite the
     exact Mathlib theorem name."*

**Threshold:** Start at 0.75 cosine similarity (same as FSDE default). Tune based on
false-positive rate during OEIS benchmark runs.

### 4.5 ProofCartographer

**Purpose:** Build a topological landmark map of the proof search space. Replace Elo-
based probabilistic search with structure-aware navigation.

**Adapted from:** `RichLearning.V2.Planning.Cartographer`.

**State definition:**
- A landmark is a `ProofState` that was reached during at least one episode.
- A transition is a `(tactic_sequence, outcome)` pair connecting two states.
- Outcome is one of: `{Progressed, Stalled, DeadEnd, Solved}`.

**Dead-end detection:**
When the same `ProofState` vector (cosine similarity > 0.92) has been visited in ≥3
episodes, all ending in `Stalled` or `DeadEnd`, the Cartographer marks that region of
the proof space as structurally exhausted. Future episodes are steered away from it
via a prompt injection: *"Prior attempts that decomposed goal X via approach Y all
failed. Approach from a different angle."*

**Landmark graph (Neo4j):**
```
(:ProofLandmark {stateVector, sorryCount, visitCount, bestOutcome})
    -[:TRANSITION {tacticSequence, outcome, episodeId}]->
(:ProofLandmark)
```

**Selection policy (replaces P-UCB / Elo):**
On each new episode, the Cartographer returns the highest-potential landmark to
resume from. Potential = `(1 - dead_end_fraction) * (1 / visit_count)`.
This is deterministic and requires no rating agents.

### 4.6 NexusProverSubagent

**Purpose:** Single episode loop — equivalent to one iteration of DeepMind's Ralph
loop, but DAPSA-gated.

**Episode flow:**
```
1. Receive (sketch, proof_state, cartographer_hint) from Orchestrator
2. ProofStateEncoder → encode state
3. DapsaEngine:
   a. PERCEIVE
   b. Query fossil vault → if hit above threshold, inject fossil tactics → go to step 6
   c. HallucinationGate scan of current sorry lemmas
   d. Build prompt: sketch + cartographer hint + hallucination warnings + fossil hints
   e. TieredLlmRouter generates next tactic block / sketch modification
4. Apply modification to sketch (search-and-replace within EVOLVE-BLOCK markers)
5. LeanOracle.CompileAsync → get LeanResult
6. If LeanResult.Compiled && SorryCount == 0 → SUCCESS, fossilize all subgoals
7. If LeanResult.Compiled && SorryCount > 0  → fossilize the compiled subgoals,
                                               continue with reduced sketch
8. If not compiled → extract errors, feed to Cartographer, continue
9. Record transition in landmark graph
10. Return updated sketch to Orchestrator
```

**Max turns per episode:** 20 LLM calls. If not solved, Orchestrator decides whether
to continue with a new episode or abandon.

### 4.7 NexusOrchestrator

**Purpose:** Manages the episode lifecycle for a single problem. Equivalent to
DeepMind's coordination layer but without the cloud parallelism.

```csharp
class NexusOrchestrator
{
    Task<ProofResult> SolveAsync(
        string leanSketchPath,
        string naturalLanguageHint,
        OrchestratorConfig config,
        CancellationToken ct);
}

class OrchestratorConfig
{
    int MaxEpisodes = 100;           // DeepMind used 3000; we use fewer due to fossil reuse
    int MaxTurnsPerEpisode = 20;
    TimeSpan EpisodeTimeout = TimeSpan.FromMinutes(10);
    bool EnableFossilReuse = true;
    bool EnableHallucinationGate = true;
    bool EnableCartographer = true;
    float FossilMatchThreshold = 0.75f;
}
```

**Episode budget rationale:** DeepMind needed 3000 episodes because each starts cold.
With fossil reuse, proven sub-goals from earlier episodes (and earlier problems) reduce
effective search depth. Target: solve problems that DeepMind needed 500+ episodes for
in ≤100 episodes via fossil injection.

---

## 5. Build Plan (Phases)

### Phase 0 — Environment Setup (Day 1–2)

```bash
# 1. Install elan and Lean 4
curl https://elan.lean-lang.org/elan-init.sh -sSf | sh
source ~/.zshrc
elan toolchain install stable && elan default stable

# 2. Create Lean project with Mathlib
cd ~/Projects/DeepMind_Nexus_Challenge
lake new NexusLean math
cd NexusLean && lake exe cache get   # download precompiled Mathlib (~30 min first time)

# 3. Clone DeepMind's formal conjectures repo (the Lean problem files)
cd ~/Projects/DeepMind_Nexus_Challenge
git clone https://github.com/google-deepmind/formal-conjectures

# 4. Create C# solution
dotnet new sln -n NexusAgent
dotnet new classlib -n NexusAgent.Core
dotnet sln add NexusAgent.Core
# Add reference to Rich-Learning-Base V2
dotnet add NexusAgent.Core reference \
  ../../Rich-Learning-Base/src/RichLearning.V2/RichLearning.V2.csproj
```

**Deliverable:** `lean --version` works, `lake build` succeeds in NexusLean project,
`dotnet build` succeeds in NexusAgent.Core.

### Phase 1 — LeanOracle (Day 2–3)

Implement `ILeanOracle` with process shell-out, stdout/stderr parsing, compile caching
in Neo4j. Write unit tests verifying:
- A trivially correct Lean theorem returns `Compiled = true, SorryCount = 0`.
- A theorem with one `sorry` returns `Compiled = true, SorryCount = 1`.
- A syntactically broken sketch returns `Compiled = false` with non-empty `Errors`.
- Identical sketches hit the Neo4j cache (verified by Neo4j query log).

### Phase 2 — ProofStateEncoder (Day 3–4)

Implement dim-64 encoder. Build a small Mathlib tactic vocabulary file
(`tactics_vocab.json`, ~200 entries) by scanning Mathlib source. Write unit tests
verifying:
- Two encodings of the same goal text produce cosine similarity > 0.95.
- Two encodings of unrelated goals produce cosine similarity < 0.5.
- L2 norm of any output vector is 1.0.

### Phase 3 — ProofFossilizer + Neo4j schema (Day 4–5)

Define Neo4j node labels and vector index. Implement `ProofFossilizer`. Write tests
verifying round-trip: fossilize a subgoal → retrieve it by vector similarity → confirm
retrieved tactic block matches original.

Neo4j index:
```cypher
CREATE VECTOR INDEX proofFossils IF NOT EXISTS
FOR (f:ProofFossil) ON (f.stateVector)
OPTIONS { indexConfig: { `vector.dimensions`: 64,
                         `vector.similarity_function`: 'cosine' } };
```

### Phase 4 — HallucinationGate (Day 5–6)

Implement gate logic. Write tests using synthetic sketches with planted hallucinated
lemmas vs. genuinely proved ones. Verify: false positive rate < 10%, detection rate
on planted hallucinations > 80%.

### Phase 5 — ProofCartographer (Day 6–7)

Implement landmark graph with dead-end detection. Write tests verifying:
- A state visited 3× with `DeadEnd` outcome is marked structurally exhausted.
- Selection policy returns lower-visit landmarks over higher-visit ones.
- Cartographer hint appears correctly in the LLM prompt string.

### Phase 6 — NexusProverSubagent + Orchestrator (Day 7–10)

Wire all components through `DapsaEngine`. Implement the full episode loop. Test on a
single simple OEIS conjecture end-to-end. Confirm: Qwen local or DeepSeek API called, Lean
compiler invoked, fossils written to Neo4j on success.

### Phase 7 — OEIS Benchmark Run (Week 3)

Select 50 OEIS conjectures from DeepMind's set (start with the 44 they solved — these
are tractable). Run `NexusOrchestrator` on each. Record:
- Episodes required per problem
- Fossil hit rate per episode
- Problems solved

Compare episode counts to DeepMind's reported numbers. The fossil reuse hypothesis
predicts later problems in the run should require fewer episodes than earlier ones as
the vault fills.

### Phase 8 — Analysis + Erdős Stretch (Week 4+)

Analyze fossil graph structure in Neo4j. Identify the most-reused proof fossils (the
"universal lemmas" of elementary combinatorics). Attempt 10 of the Erdős problems
that DeepMind solved — confirming we can replicate at lower cost.

---

## 6. Key Design Decisions and Rationale

### Why not parallel subagents?

DeepMind parallelizes N independent subagents because they have no shared state.
Rich Learning's graph *is* the shared state — running a second subagent in parallel
would write competing state transitions to the Cartographer graph, requiring
transactional conflict resolution. Serial episodes with graph-guided restarts are
cleaner on a single machine and more aligned with DAPSA's design.

If parallelism is desired later, each subagent can have its own in-memory Cartographer
that merges into Neo4j at episode end (similar to the MetaLevelBuilder pattern).

### Why Qwen3.6 local + DeepSeek V4 API and not Gemini Pro?

Three reasons:

1. **Cost.** DeepSeek V4-Pro is $0.435/M input ($0.003625/M cached) and $0.87/M output as of May 2026 (the 75%-off promo became permanent on 2026-05-22). With a stable prompt prefix in the agent loop, cache hit rate sits at 60–80%, giving an effective per-problem cost of ~$3 vs. ~$32 on Gemini Pro.

2. **Framework demonstration.** If Rich Learning's fossil reuse and Cartographer let a cheap tiered stack match results that DeepMind needed Gemini 3.1 Pro for, that is direct evidence of the framework's value — the graph compensates for raw model gap.

3. **Local-first economics.** Qwen3.6-35B-A3B is a Mixture-of-Experts model with 35B total but only 3B active parameters. It fits in ~5 GB RAM and runs fast on the M4 Pro. Free tier handles screening, hallucination classification, Cartographer dead-end detection, and the first 3 turns of every episode. Only escalates to paid tiers when local stalls.

### Why encode proof state as dim-64 rather than using a full embedding model?

Three reasons: (1) The `DefaultStateEncoder` in Rich Learning V2 establishes the
pattern of compact, interpretable, deterministic encodings rather than opaque neural
embeddings — consistency with the V2 design philosophy. (2) A 200-tactic vocabulary
captures 95%+ of Mathlib proof patterns. (3) Compact vectors are fast to index and
query in Neo4j.

If this proves too coarse, upgrade to a 512-dim embedding using a local embedding
model (e.g., `nomic-embed-text`) in Phase 8 — the IStateEncoder interface makes this
a drop-in replacement.

### Why not use AlphaProof (DeepMind's RL prover) as a tool?

AlphaProof is not publicly available. This project intentionally excludes it to
demonstrate that Rich Learning's graph — combined with a general-purpose local LLM —
can compensate for the absence of a specialized RL prover.

---

## 7. Success Metrics

| Metric | Target | DeepMind Baseline |
|---|---|---|
| OEIS conjectures solved | ≥ 10 (from local 50-problem subset) | 44/492 |
| Average episodes per solved problem | ≤ 200 | ~500 (estimated) |
| Fossil hit rate (after 20 problems) | ≥ 30% of episodes | 0% (no fossil system) |
| Hallucination intercepts | ≥ 5 in 50-problem run | Not measured |
| LLM cost (estimated) | ≤ $150 total (Qwen free + DeepSeek V4 cached) | ~$100–300/problem |
| Erdős problems solved (stretch) | ≥ 1 | 9/353 |

---

## 8. Risks and Mitigations

**Risk 1: Qwen3.6-35B-A3B alone is weaker than Gemini 3.1 Pro**  
Mitigation: The fossil vault compensates for weaker generation by reusing known-good
tactic blocks. For the OEIS set (simpler problems), the gap should be manageable.
DeepSeek V4-Pro is the drop-in escalation tier with prefix caching keeping costs low.

**Risk 2: ProofStateEncoder dim-64 is too coarse for meaningful fossil matching**  
Mitigation: Test on Phase 3 synthetic benchmarks. Upgrade to dim-512 with a local
embedding model if cosine similarity < 0.5 on clearly related proof states.

**Risk 3: Lean compilation latency per episode is too high for 100-episode budget**  
Mitigation: Neo4j sketch cache (Phase 1). Lean's `--server` mode (language server
protocol) maintains compilation state across calls — much faster than cold compilation.
Target: < 2 seconds per compilation with warm cache.

**Risk 4: DeepMind's Lean formalizations use Mathlib APIs that changed**  
Mitigation: Clone the exact Lean / Mathlib version pinned in DeepMind's
`formal-conjectures` repo (`lean-toolchain` file). `elan` handles version pinning
automatically.

---

## 9. File Layout

```
DeepMind_Nexus_Challenge/
├── SPEC.md                          ← this document
├── README.md                        ← short project summary
├── NexusLean/                       ← Lean 4 project (lake)
│   ├── lakefile.toml
│   ├── lean-toolchain
│   └── NexusLean/
│       ├── Basic.lean               ← scratch file
│       └── Problems/                ← local copies of target problems
│           ├── OEIS/
│           └── Erdos/
├── NexusAgent/                      ← C# solution
│   ├── NexusAgent.sln
│   ├── NexusAgent.Core/
│   │   ├── NexusAgent.Core.csproj
│   │   ├── Oracle/
│   │   │   ├── ILeanOracle.cs
│   │   │   └── LeanOracle.cs
│   │   ├── Encoding/
│   │   │   ├── ProofState.cs
│   │   │   └── ProofStateEncoder.cs
│   │   ├── Memory/
│   │   │   ├── ProofFossil.cs
│   │   │   └── ProofFossilizer.cs
│   │   ├── Safety/
│   │   │   └── HallucinationGate.cs
│   │   ├── Planning/
│   │   │   ├── ProofLandmark.cs
│   │   │   └── ProofCartographer.cs
│   │   ├── Agent/
│   │   │   ├── NexusProverSubagent.cs
│   │   │   └── NexusOrchestrator.cs
│   │   └── Benchmarks/
│   │       ├── OeisBenchmark.cs
│   │       └── ErdosBenchmark.cs
│   └── NexusAgent.Tests/
│       └── (mirror of Core structure, unit tests)
├── formal-conjectures/              ← git submodule: google-deepmind/formal-conjectures
├── data/
│   ├── tactics_vocab.json           ← 200-entry Lean 4 tactic vocabulary
│   ├── oeis_selected_50.json        ← 50 OEIS problems selected for Phase 7
│   └── results/                     ← benchmark run outputs
└── docs/
    ├── architecture.md              ← component interaction diagram
    └── neo4j_schema.cypher          ← full Neo4j schema DDL
```

---

## 10. Neo4j Schema (Full DDL)

```cypher
// Proof fossils — proven sub-goals
CREATE CONSTRAINT proof_fossil_id IF NOT EXISTS
  FOR (f:ProofFossil) REQUIRE f.id IS UNIQUE;

CREATE VECTOR INDEX proofFossils IF NOT EXISTS
  FOR (f:ProofFossil) ON (f.stateVector)
  OPTIONS { indexConfig: {
    `vector.dimensions`: 64,
    `vector.similarity_function`: 'cosine'
  }};

// Proof landmarks — visited proof states
CREATE CONSTRAINT proof_landmark_id IF NOT EXISTS
  FOR (l:ProofLandmark) REQUIRE l.id IS UNIQUE;

CREATE VECTOR INDEX proofLandmarks IF NOT EXISTS
  FOR (l:ProofLandmark) ON (l.stateVector)
  OPTIONS { indexConfig: {
    `vector.dimensions`: 64,
    `vector.similarity_function`: 'cosine'
  }};

// Problem registry
CREATE CONSTRAINT problem_id IF NOT EXISTS
  FOR (p:MathProblem) REQUIRE p.id IS UNIQUE;

// Relationships
// (:ProofFossil)-[:PRECEDES]->(:ProofFossil)
// (:ProofFossil)-[:PROVED_IN]->(:MathProblem)
// (:ProofLandmark)-[:TRANSITION {tacticSequence, outcome}]->(:ProofLandmark)
// (:ProofLandmark)-[:BELONGS_TO]->(:MathProblem)
```

---

## 11. Starting Point: Run DeepMind's Own Proofs First

Before writing a single line of NexusAgent, verify the local Lean environment by
compiling DeepMind's already-published proofs from the `alphaproof-nexus-results` repo.
This confirms Lean + Mathlib is correctly installed and gives intuition for what the
proof sketches look like.

```bash
git clone https://github.com/google-deepmind/alphaproof-nexus-results
cd alphaproof-nexus-results
# examine the proof files, pick one small OEIS proof
# copy it into NexusLean/Problems/OEIS/
lake build   # should compile clean
```

If the proofs compile locally: environment confirmed, proceed to Phase 1.  
If not: check Lean version pinning in `lean-toolchain`, run `lake exe cache get` again.

---

*Document version: 1.0 — May 2026*  
*Next review: after Phase 3 (ProofFossilizer) — update fossil schema based on actual
Neo4j vector index performance.*
