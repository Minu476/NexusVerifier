# NexusAgent — Opus Handoff

**Date**: 2026-05-26  
**From**: Sonnet 4.6 (prior session)  
**To**: Opus 4.7 (incoming)  
**Budget remaining**: ~$199.11 of $200.00

---

## 1. Project Goal

Prove as many Erdős problems as possible in Lean 4 / Mathlib using an autonomous proof-search
agent, beating (or complementing) Google DeepMind's AlphaProof results. The benchmark is the
accepted transitions
with zero `sorry` after the agent runs.

---

## 2. Repository Layout

```
/Users/nassertowfigh/Projects/DeepMind-Nexus-Challenge/
├── NexusAgent/                          ← C# agent solution
│   ├── NexusAgent.sln
│   ├── NexusAgent.Core/                 ← all agent logic
│   │   ├── Agent/
│   │   │   ├── NexusProverSubagent.cs   ← episode loop (RECENTLY FIXED)
│   │   │   └── NexusOrchestrator.cs     ← multi-episode driver
│   │   ├── Memory/
│   │   │   ├── Neo4jClient.cs           ← Neo4j proof-fossil store (FIXED this session)
│   │   │   ├── ProofFossilizer.cs       ← fossil CRUD + retrieval
│   │   │   └── INeo4jClient.cs
│   │   ├── Llm/TieredLlmRouter.cs       ← Qwen → DeepSeek Flash → DeepSeek Reasoner
│   │   ├── Oracle/LeanOracle.cs         ← spawns `lake env lean` process
│   │   ├── Planning/ProofCartographer.cs ← Neo4j landmark graph
│   │   ├── Prompts/PromptBuilder.cs
│   │   └── Safety/HallucinationGate.cs
│   ├── NexusAgent.Cli/Program.cs        ← `bench` subcommand entry point
│   └── NexusAgent.Tests/
├── formal-conjectures/                  ← Lean 4 problem files (DeepMind repo)
│   └── FormalConjectures/ErdosProblems/ ← 1.lean, 10.lean, …, 1104.lean …
├── data/
│   ├── erdos_phase9_ams5/               ← 75 Lean files staged for Phase 9 (AMS-5)
│   └── results/                         ← bench JSON/HTML outputs
├── SOLVED_PROBLEMS.md                   ← reference list of all formally proved problems
└── HANDOFF_OPUS.md                      ← this file
```

---

## 3. Infrastructure

| Component | Details |
|-----------|---------|
| **Neo4j** | Enterprise 2025.11.2, `nexusdb` at `bolt://localhost:7687`, user `neo4j`, pw `REDACTED` |
| **Lean 4** | Lake project at `formal-conjectures/`. Temp files written to `formal-conjectures/_nexus_tmp/`. 30s compile timeout. |
| **Ollama (Tier 1)** | `qwen3:6b-a3b` at `http://localhost:11434` — free, fast |
| **DeepSeek Flash (Tier 2)** | `deepseek-chat` — $0.27/M input / $1.10/M output |
| **DeepSeek Reasoner (Tier 3)** | `deepseek-reasoner` — $0.55/M input / $2.19/M output, `max_tokens=8192` |

---

## 4. Build & Run

```bash
# Build (always from solution root)
cd /Users/nassertowfigh/Projects/DeepMind-Nexus-Challenge/NexusAgent
dotnet build NexusAgent.sln -c Release --no-restore

# Run Phase 9 benchmark (75 AMS-5 problems)
cd /Users/nassertowfigh/Projects/DeepMind-Nexus-Challenge
dotnet run --project NexusAgent.Cli -c Release --no-build -- \
  bench data/erdos_phase9_ams5 \
  --source Erdos \
  --max-episodes 5 \
  --max-turns 12 \
  --budget 199.11 \
  >> NexusAgent/NexusAgent.Cli/bin/Release/net10.0/logs/nexus-20260526.log 2>&1 &

# Tail the log
tail -f NexusAgent/NexusAgent.Cli/bin/Release/net10.0/logs/nexus-20260526.log
```

---

## 5. Current Status

### Problems Solved (15 unique, both phases)

| # | Problem | Category | Notes |
|---|---------|----------|-------|
| 1 | Erdős #1 | Combinatorics | Sum-distinct sets, weak lower bound |
| 2 | Erdős #10 | Number Theory | Primes + powers of 2 (Gallagher variant) |
| 3 | Erdős #48 | Number Theory | φ(n) = σ(m) infinitely often |
| 4 | Erdős #107 | Geometry | Happy ending / convex polygon existence |
| 5 | Erdős #109 | Combinatorics | Erdős sumset conjecture (Moreira-Richter-Robertson) |
| 6 | Erdős #139 | Combinatorics | Szemerédi (r_k(N) = o(N)) |
| 7 | Erdős #194 | Combinatorics | Monotone AP in reorderings of ℝ — disproved |
| 8 | Erdős #204 | Number Theory | Covering systems with divisors — disproved (Adenwalla) |
| 9 | Erdős #219 | Number Theory | Green–Tao (arbitrarily long prime APs) |
| 10 | Erdős #228 | Analysis | Flat Littlewood polynomials (Balister et al.) |
| 11 | Erdős #239 | Number Theory | Mean of ±1 multiplicative functions (Wirsing/Halász) |
| 12 | Erdős #248 | Number Theory | ω(n+k) ≪ k for infinitely many n |
| 13 | Erdős #250 | Number Theory | Σ σ(n)/2^n is irrational (Nesterenko) |
| 14 | Erdős #1077 | Graph Theory | D-balanced dense subgraph — disproved |
| 15 | Erdős #1084 | Geometry | Unit-distance pairs among 1-separated points (3 variants) |

### Phase 9 Second Run — Detailed Results (8 problems, killed 2026-05-26 ~17:10)

| Problem | Outcome | Episodes | Cost | Fossil avgSim | Notes |
|---------|---------|----------|------|---------------|-------|
| Erdos_1 | ✅ Solved | 2 | $0.114 | 0.802 | Ep0 MaxTurns, Ep1 Solved (8 turns) |
| Erdos_10 | ✅ Solved | 5 | $0.393 | 0.000 | Ep0-3 MaxTurns, Ep4 Qwen 1 turn |
| Erdos_107 | ✅ Solved | 2 | $0.034 | 0.904 | Ep0 MaxTurns (fossil=7), Ep1 Solved |
| Erdos_1077 | ✅ Solved | 2 | $0.108 | 0.988→0 | Ep0 MaxTurns, Ep1 Solved (8 turns) |
| Erdos_1084 | ✅ Solved | 1 | $0.045 | 0.871 | Ep0 Solved (7 turns) |
| Erdos_1085 | ❌ Failed | 5 | $0.034 | 0.916 | Degenerate loop: Ep1–4 each 12/12 fossil hits |
| Erdos_109 | ✅ Solved | 2 | $0.048 | 0.934 | Ep0 MaxTurns, Ep1 Solved (11 turns) |
| Erdos_1104 | ⏳ Killed | 1 | $0.107 | 0.903 | Ep0 MaxTurns; session killed |

**Phase 9 score so far: 6/8 completed problems solved. 67 problems remaining.**  
**Budget spent: ~$0.89. Remaining: ~$199.11.**

---

## 6. Bugs

### Fixed This Session

**A) CrossRun Cypher NULL bug** — `Neo4jClient.cs` ~line 138  
Phase 8 fossils have `runId = NULL`. The original Cypher condition `f.runId IS NOT NULL AND f.runId <> $runId` never tagged them as cross-run. Fixed to `f.runId IS NULL OR f.runId <> $runId`.

**B) Direct-substitution loop** — `NexusProverSubagent.cs` (fixed just now, build passes)  
After a fossil direct-sub failed Lean validation, the sketch was always updated to the broken
version (even on `DeadEnd`/`Stalled`). On the next turn the same fossil was retrieved (state
unchanged → same embedding → same top result) and retried. Erdos_1085 Ep1–4 each did exactly
12/12 fossil hits with `avgSim=0.916` — all the same failed substitution.

**Fix applied**: 
- `triedFossils HashSet<string>` per episode
- `directSubFossilId` captured in fossil branch
- After compile: if outcome is `DeadEnd | Stalled` and it was a direct-sub → `triedFossils.Add(id)` + `continue` (sketch/lastResult/prevState are NOT updated, reverting to pre-sub state)
- `TryFossilHitAsync` now filters out `triedFossils` from `FindCandidatesAsync` results

### Remaining Bugs / Weaknesses

**1. Fossil vault contamination from old runs**  
Fossils from Phase 8 may have wrong `runId = NULL`. They can be retrieved at high similarity
for Phase 9 problems but fail Lean (different Mathlib import paths, different namespace
structure). Symptom: Erdos_107 Ep0 had `fossil=7` on 12 turns but still failed.  
**Suggested fix**: Add a `CompilationVerified : bool` property to `ProofFossil`. After a direct-sub succeeds Lean, mark the fossil verified. When retrieving, prefer verified fossils or downrank unverified cross-run fossils.

**2. LLM prompt doesn't include full Lean error messages**  
When Lean rejects a tactic, the LLM only gets the pending goals. The actual type-mismatch
error text would give the LLM crucial signal. Check `PromptBuilder.BuildProverRequest` — it
receives `warnings` from `HallucinationGate` but the Lean compile error messages from
`prevState.ErrorMessages` may not be prominently surfaced in the prompt.

**3. Tier routing burns budget on long timeouts**  
Erdos_10 spent $0.393 across 5 episodes, mostly on Tier 3 (DeepSeek Reasoner) timeouts.
The `TieredLlmRouter` escalates tier based on `TurnsSinceLastProgress`. This escalates too
aggressively for hard problems that need many short steps. Consider: escalate to Tier 3
only if `TurnsSinceLastProgress ≥ 6` (currently likely 3 or 4).

**4. No resume / skip-already-solved logic**  
If Phase 9 is restarted, it re-runs all problems from scratch (including ones already solved
and fossilized). The `bench` command should skip problems whose sketch compiles with 0 sorry.
Check `Program.cs`'s `RunBenchAsync` — currently iterates all files unconditionally.

**5. `SubstituteFirstSorry` replaces only the FIRST `sorry` in the sketch**  
If there are multiple sorries, subsequent turns may try to substitute the same fossil into
sorries 2, 3, … with similar (failing) results. Once a fossil is blacklisted (fix B above),
this is largely mitigated, but the agent still can't target a specific sorry.

---

## 7. Immediate Next Tasks

### Priority 1: Restart Phase 9 with fixed binary

The fix (B) is in and built. Restart the benchmark. 67 problems remain.

```bash
cd /Users/nassertowfigh/Projects/DeepMind-Nexus-Challenge
dotnet run --project NexusAgent.Cli -c Release --no-build -- \
  bench data/erdos_phase9_ams5 \
  --source Erdos \
  --max-episodes 5 \
  --max-turns 12 \
  --budget 199.11 \
  >> NexusAgent/NexusAgent.Cli/bin/Release/net10.0/logs/nexus-phase9-v2.log 2>&1 &
```

Note: the benchmark iterates alphabetically. The 8 problems above have already run; check
whether the CLI skips them or re-runs. If it re-runs, the already-solved ones will solve
quickly via fossil direct-sub (now working correctly since triedFossils resets per episode).

### Priority 2: Surface Lean errors in LLM prompt

In `PromptBuilder.BuildProverRequest`, add a section:
```
### Lean Errors (last compile)
<paste prevState.ErrorMessages here>
```
This is likely the single highest-leverage prompt improvement available.

### Priority 3: Downrank unverified cross-run fossils

In `ProofFossilizer.FindCandidatesAsync`, after retrieving matches, apply a penalty:
```csharp
if (match.Fossil.CrossRun && !match.Fossil.CompilationVerified)
    match = match with { Similarity = match.Similarity * 0.85f };
```
This prevents old Phase 8 fossils from monopolising the top-K slots.

---

## 8. Key File Paths (Absolute)

| What | Path |
|------|------|
| Agent solution | `/Users/nassertowfigh/Projects/DeepMind-Nexus-Challenge/NexusAgent/NexusAgent.sln` |
| Episode loop | `NexusAgent.Core/Agent/NexusProverSubagent.cs` |
| Orchestrator | `NexusAgent.Core/Agent/NexusOrchestrator.cs` |
| Fossil store | `NexusAgent.Core/Memory/Neo4jClient.cs` |
| Fossilizer | `NexusAgent.Core/Memory/ProofFossilizer.cs` |
| LLM router | `NexusAgent.Core/Llm/TieredLlmRouter.cs` |
| Prompt builder | `NexusAgent.Core/Prompts/PromptBuilder.cs` |
| Lean oracle | `NexusAgent.Core/Oracle/LeanOracle.cs` |
| CLI entry | `NexusAgent.Cli/Program.cs` |
| Phase 9 data | `/Users/nassertowfigh/Projects/DeepMind-Nexus-Challenge/data/erdos_phase9_ams5/` |
| Lean problems | `/Users/nassertowfigh/Projects/DeepMind-Nexus-Challenge/formal-conjectures/FormalConjectures/ErdosProblems/` |
| Today's log | `NexusAgent/NexusAgent.Cli/bin/Release/net10.0/logs/nexus-20260526.log` |
| Results JSON | `data/results/` |
| Solved reference | `/Users/nassertowfigh/Projects/DeepMind-Nexus-Challenge/SOLVED_PROBLEMS.md` |

---

## 9. Notes on Agent Architecture

The agent follows a DAPSA loop (Decompose → Attempt → Plan → Search → Archive):

```
EpisodeContext (problemId, sketch, budget)
    ↓
NexusProverSubagent.RunEpisodeAsync()
    for each turn:
        1. TryFossilHitAsync()   ← cosine sim over Neo4j dim-64 StateVector embeddings
           └─ if sim ≥ threshold (default 0.90): direct-substitute fossil TacticBlock into first sorry
           └─ else: pass as hint to LLM
        2. HallucinationGate.ScanAsync()  ← regex/rule checks for common hallucinations
        3. ProofCartographer.GetDeadEndHintAsync() ← avoid previously failed tactic patterns
        4. TieredLlmRouter.SendAsync()  ← Qwen / Flash / Reasoner based on progress stall count
        5. LeanOracle.CompileAsync()    ← 30s timeout, spawns `lake env lean`
        6. ClassifyOutcome()            ← Solved / Progressed / Stalled / DeadEnd
        7. ProofCartographer.RecordTransitionAsync() ← landmark graph
        8. ProofFossilizer.FossilizeAsync() ← on sorry reduction
    ↓
NexusOrchestrator  ← runs up to 5 episodes, tracks budget
```

The fossil vault stores `ProofFossil` nodes in Neo4j with:
- `StateVector`: float[64] embedding of the proof state (pending goals + hypotheses)

---

## 10. Deterministic Graph-First Planner Spec (v1.5)

This section specifies the production search flow for graph-first proving in NexusAgent.
It is intended to be executable, not aspirational prose.

### 10.1 Objective

Given a live Lean goal state, search only over kernel-checkable transitions and minimize
expected closure cost of all unresolved sub-goals.

### 10.2 State Model

- **State identity**: `ProofStateEncoder.ComputeCanonicalStateHash` over symbolic canonical state.
- **Node type**: planner node stores sketch, Lean compile result, canonical hash, depth.
- **Closed state**: `LeanResult.IsFullyProved == true`.

### 10.3 Expansion Contract

At each node:

1. Encode state vector (`ProofStateEncoder.Encode`).
2. Query graph proposals (`INeo4jClient.ProposeTacticsFromGoalVectorAsync`).
3. For each candidate tactic:
   - substitute first `sorry` in sketch,
   - compile with Lean oracle,
   - reject if compile fails,
   - reject if structural validator fails,
   - admit otherwise and enqueue.

All accepted transitions are Lean-validated before they influence search.

### 10.4 Cost Function (Expected Closure Cost)

Priority is not plain similarity; it is a weighted expected closure estimate:

$$
J = S + w_d d + w_r (1-r) + w_h (1-h) + w_b \log_2(1+g) + w_e e - w_n I
$$

Where:

- $S$: current `sorry` count
- $d$: depth
- $r$: graph rank score
- $h$: historical success rate
- $g$: number of pending goals after candidate application
- $e$: normalized compile error signal
- $I$: improvement indicator (1 if candidate reduces sorry count vs parent, else 0)

Lower $J$ is better.

### 10.5 Search Guards

To prevent combinatorial blow-up:

- **State-visit cap**: each canonical hash has max expansion count per run.
- **Per-node sketch dedup**: candidates producing identical sketch hashes in one expansion are pruned.
- **Transposition pruning**: if a canonical hash was already seen with lower/equal sorry count, prune.
- **Global expansion cap**: existing `PlannerMaxExpansions` remains hard bound.

### 10.6 Current and Planned Retrieval Modes

- **Current (implemented)**: vector-nearest GoalShape -> aggregated outgoing tactics.
- **Planned (next)**: typed backward retrieval over conclusion-shape compatibility,
  merged with forward local tactic proposals into one AND-OR frontier.

### 10.7 Correctness Boundary

Graph retrieval is advisory only.
Proof correctness comes exclusively from Lean compilation checks on each accepted transition.

### 10.8 Operational Metrics

Per planner run we track:

- expansions
- accepted transitions
- compile rejects
- structural rejects
- cycle/state-visit prunes
- duplicate-sketch prunes

These metrics are logged and included in planner result payloads for future bench reporting.

### 10.9 Config Surface

`OrchestratorConfig` includes planner weights and guards:

- `PlannerStateVisitCap`
- `PlannerDepthWeight`
- `PlannerRankWeight`
- `PlannerSuccessWeight`
- `PlannerBranchingWeight`
- `PlannerErrorWeight`
- `PlannerNoveltyBonus`

CLI flags exist for all of the above in `NexusAgent.Cli` `bench` mode.

---

## 10. DeepMind Comparison

DeepMind AlphaProof solved 7 problems (#12, #26, #125, #138, #152, #741, #846).  
NexusAgent has solved **15 problems** (#1, #10, #48, #107, #109, #139, #194, #204, #219, #228,
#239, #248, #250, #1077, #1084). No overlap — different problems.  
See `SOLVED_PROBLEMS.md` for full mathematical descriptions of all 22 combined.

---

## 11. Architecture Decision Update (2026-05-27)

This supersedes the previous assumption that the primary proof graph should be assembled through
InfoTree JSONL -> C# ingestor -> CSV -> Neo4j.

### 11.1 Finalized Core Model: Lean-Native Hypergraph

The canonical structure is a **directed hypergraph** built from declaration types already present
in Lean's environment.

- A declaration can consume multiple premises and produce one or more goal shapes.
- Therefore this is not a simple edge model; it is an AND/OR-compatible hyperedge model.

Lean-side logical model:

```text
Node:      HashMap<GoalHash, ProofStatus>

HyperEdge: {
    function: Lean.Name,
    inputs:   [GoalHash],
    outputs:  [GoalHash]
}

Index:     HashMap<GoalHash, [HyperEdge]]   -- reverse lookup by output hash
```

### 11.2 Extraction Principle

Primary graph construction is from `Environment.constants` only:

```text
for decl in env.constants do
  let inputs  := canonicalInputGoalHashes(decl.type)
  let outputs := canonicalOutputGoalHashes(decl.type)
  add HyperEdge(decl.name, inputs, outputs)
```

No external transport layers are required for this core graph build.

### 11.3 Search Semantics

Backward proof search is AND/OR over hyperedges:

- OR: many functions may produce a target goal hash.
- AND: one chosen function is usable only when all required input hashes are proven.

### 11.4 Role of InfoTree (Narrowed)

InfoTree is still useful, but only for dynamic/intra-proof telemetry:

- extracting intermediate sub-goal transitions inside a specific proof script,
- collecting tactic-level traces not represented by top-level declarations.

InfoTree is **not** the source of truth for declaration-level connectivity.

### 11.5 Immediate Execution Policy

- Pause further complexity in JSONL/CSV/Neo4j edge-building paths for declaration connectivity.
- Continue only Lean-native hypergraph implementation for core planning.
- Re-introduce InfoTree as an optional secondary channel after core hypergraph search is stable.

### 11.6 Design Freeze (Final)

Status: **Finalized** (2026-05-27).

The following are now frozen design decisions for core architecture:

- Source of truth for theorem-connectivity edges is Lean environment declarations and their types.
- Core structure is a directed hypergraph over goal-shape hashes, not a simple directed graph.
- Search semantics are AND/OR backward chaining over hyperedges.
- InfoTree remains a secondary telemetry channel for intra-proof transitions only.
- External transport layers (JSONL/CSV/Neo4j) are non-authoritative for declaration-edge construction.

Any future changes to these points should be treated as an explicit architecture revision, not incremental tuning.

## 12. Open Questions for Opus

These are unresolved design questions the owner wants addressed before or during Phase 9
continuation. Please read the relevant code and give a concrete recommendation for each.

**Q1 — Should we support proof assistants other than Lean 4?**

The `formal-conjectures` benchmark is Lean 4 / Mathlib only, so the *benchmark target* is
fixed. But there are proven formal proof ecosystems that could be useful in different roles:

| System | Foundation | Automation | Mathlib equivalent | Relevant here? |
|--------|-----------|------------|-------------------|----------------|
| **Coq / Rocq** | CIC (Calculus of Inductive Constructions) | `omega`, `ring`, `tauto`, Ltac2 | `MathComp` (SSReflect) | Mature, large community, some tactic patterns differ from Lean |
| **Isabelle/HOL** | Higher-order logic | `auto`, `simp`, `sledgehammer` (calls ATPs) | `AFP` (Archive of Formal Proofs) | `sledgehammer` integration with external ATP solvers is unique |
| **HOL Light** | Simple type theory | `MESON`, `METIS` | Very limited | Used in Flyspeck (Kepler conjecture); minimal library |
| **Lean 4 / Mathlib** | CIC + universes | `decide`, `norm_num`, `aesop`, `polyrith` | Mathlib4 (largest active) | **Our current target** |

**Specific sub-questions**:
- Could Isabelle's `sledgehammer` (which calls external ATP provers like Vampire, E, Z3) be
  used as an *oracle* to discover tactic proofs, which are then *transcribed* to Lean 4?
  This is architecturally possible: run Isabelle on a parallel problem statement, extract
  the proof structure, and use it to guide the Lean LLM prompt.
- Are there problems in our AMS-5 set where Mathlib has weak coverage but MathComp (Coq)
  would have native lemmas? If so, is cross-system translation worth the engineering cost?
- For the hallucination gate: should we add Coq/Isabelle tactic names to the block-list
  (e.g. `exact`, `apply` mean the same in Lean, but `intros` / `revert` have subtleties)?

**Owner's instinct**: We are Lean-only for now. But if Opus sees a class of problems where
Lean's `simp` / `aesop` are systematically weaker than Isabelle's `sledgehammer`, it's worth
flagging as a future multi-engine path.

---

**Q2 — Is the 64-dimensional StateVector embedding rich enough?**

`ProofStateEncoder` produces a `float[64]` vector from the pending goals + hypotheses text.
At 64 dims, the cosine similarity is noisy for short goal strings. We see false positives
(sim=0.916 fossil retrieved for Erdos_1085 every episode, never helps). Review
`NexusAgent.Core/Encoding/ProofStateEncoder.cs` and answer:
- What encoding method is used (bag-of-words? sentence embedding? hash projection)?
- Would upgrading to 256 or 512 dims (or a proper sentence encoder) reduce false positives?
- Is the retrieval threshold (0.90) calibrated empirically or chosen arbitrarily?

---

**Q3 — Should episodes share proof state across restarts?**

Currently each episode starts from `InitialSketch` (the original Lean file). A solved Ep0
sketch is **not** passed as `InitialSketch` to Ep1. This means good partial progress is
discarded between episodes. The fossil vault partially compensates, but only at the subgoal
level. Assess: would passing the *best sketch seen so far* (lowest sorry count) as
`InitialSketch` for subsequent episodes improve solve rates, and what's the risk of
amplifying errors from a bad partial proof?
