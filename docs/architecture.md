# Architecture

**Date:** 2026-07-27 (unified rewrite) · **Author:** GLM-5.2 (ZCode) · **Audience:** contributors
and researchers who need the internal picture. This is the unified architecture doc, replacing the
older 61-line stub (which is preserved at the repo root as `architecture.md` until that duplicate
is removed). Every component and data flow is source-cited.

> The README and [`CONCEPTS.md`](CONCEPTS.md) give the *what*; this doc gives the *how*. For
> configuration, see [`CONFIGURATION.md`](CONFIGURATION.md); for commands, see
> [`CLI_REFERENCE.md`](CLI_REFERENCE.md).

---

## 1. System overview

NexusVerifier is four subsystems working together:

```
                 ┌─────────────────────────────────────────────┐
                 │            User / CLI (`nexus`)             │
                 │   ingest-parts | solve | bench | scan-hg …  │
                 └────────────────────┬────────────────────────┘
                                      │
          ┌───────────────────────────┼───────────────────────────┐
          ▼                           ▼                           ▼
 ┌─────────────────────┐   ┌──────────────────────┐   ┌─────────────────────┐
 │  1. Lean oracle     │   │  2. Agent pipeline   │   │  3. Neo4j graph     │
 │  (NexusAgent.Core/  │   │  (NexusAgent.Core/   │   │  backend            │
 │   Oracle/)          │   │   Agent/, Planning/, │   │  - :ProofFossil     │
 │                     │   │   Safety/, Llm/)     │   │  - :ProofLandmark   │
 │  lake env lean      │   │                      │   │  - :MathProblem     │
 │  + #print axioms    │   │  Tiered LLM router   │   │  - :LeanCompileCache│
 │  → binary truth     │   │  HallucinationGate   │   │  - :HyperedgeRecord │
 │  (cached in Neo4j)  │   │  ProofCartographer   │   │  (vector indexes)   │
 └──────────┬──────────┘   └──────────┬───────────┘   └──────────▲──────────┘
            │                         │                          │
            │  shells out             │  reads/writes            │
            ▼                         ▼                          │
 ┌─────────────────────┐   ┌──────────────────────────────────────┘
 │  formal-conjectures │   │  4. Topos integration (in-process, ephemeral)
 │  (separate repo,    │   │  - ProofGoalGraph  (per-SolveAsync run)
 │   NEXUS_LEAN_PROJECT│   │  - AND-OR backward chainer
 │   path)             │   │  Both backed by Topos HypergraphKernel, vendored
 │                     │   │  as a git submodule at external/Topos
 └─────────────────────┘   └────────────────────────────────────────
```

**The single most important relationship:** the **Lean oracle is the only authoritative signal**
in the entire system. `[verified:src=NexusAgent/NexusAgent.Core/Oracle/ILeanOracle.cs:11-27]`
Everything else — the agent pipeline, the fossil vault, the Topos graphs, the LLM tiers — is
*prompt context* that influences what sketch gets tried next. None of it influences the
`Compiled`/`SorryCount`/`IsFullyProved` verdicts, which come solely from running Lean.

---

## 2. Subsystem 1 — The Lean oracle

**Files:** `NexusAgent/NexusAgent.Core/Oracle/{ILeanOracle,LeanOracle,LeanProcessLauncher}.cs`

The binary ground-truth judge. Given a Lean sketch, it answers: did Lean accept it? How many
`sorry` placeholders remain? What's the axiom closure?

### The contract

```csharp
public interface ILeanOracle
{
    Task<LeanResult> CompileAsync(string leanSketch, CancellationToken ct);
    Task<LeanResult> CheckSubgoalAsync(string goalStatement, string proofTactics,
                                       IEnumerable<string> imports, CancellationToken ct);
}
```

`[verified:src=NexusAgent/NexusAgent.Core/Oracle/ILeanOracle.cs:11-27]`

### How it works

1. Writes the sketch to a temp file under `Path.GetTempPath()/_nexus_tmp/`.
2. Shells out via `LeanProcessLauncher.RunAsync` to `lake env lean <tmpfile>` against the
   `NEXUS_LEAN_PROJECT` checkout. `[verified:src=NexusAgent/NexusAgent.Core/Oracle/LeanOracle.cs:151]`
3. Parses stdout/stderr for compile errors and `sorry` count.
4. **(Since the 2026-07-25 sorry-citation-laundering fix)** appends a `#print axioms` probe to
   every theorem/lemma the sketch declares. Citing an already-`sorry`'d declaration compiles clean
   with `SorryCount=0` and no warning at the citing site; only `#print axioms` reveals the
   inherited `sorryAx`. `[verified:src=NexusAgent/NexusAgent.Core/Oracle/LeanOracle.cs:113-122]`
5. Caches the result in Neo4j as a `:LeanCompileCache` node keyed by SHA-256 of the sketch.
   `[verified:src=NexusAgent/NexusAgent.Core/Oracle/LeanOracle.cs:37-50]`

### Why this matters — the four gates the 2026-07-26 cycle fixed

`[verified:src=README.md:34-49]`

1. A `LeanOracle` compile check that could report `Compiled=true` on output Lean had actually
   rejected (`||` vs `&&` logic error, plus a stale error regex against a newer Lean diagnostic
   format).
2. A `SketchValidator` structural gate that could be defeated by substring name matching or
   shadowing the original declaration with a new `def`.
3. Sorry-citation-launder (above).
4. A stale DeepSeek model-name mapping causing silent API failures.

Plus a fifth issue — a corpus-selection error, not a pipeline bug — caught and written up
separately in [`FC100_GAP_SET_METHODOLOGY.md`](FC100_GAP_SET_METHODOLOGY.md).

---

## 3. Subsystem 2 — The agent pipeline

**Files:** `NexusAgent/NexusAgent.Core/{Agent,Planning,Safety,Llm}/`

The proof-search engine. Three layers, plus the safety/memory subsystems that wrap them.

### Layer 1 — `NexusOrchestrator`

Manages the episode lifecycle for one problem. **Serial by design** — the graph is shared state,
and serial episodes with graph restarts are cleaner than parallel conflicting writes.
`[verified:src=NexusAgent/NexusAgent.Core/Agent/NexusOrchestrator.cs:16]`

```csharp
public sealed class NexusOrchestrator
{
    Task<ProofResult> SolveAsync(ProblemInput problem, OrchestratorConfig config, CancellationToken ct);
}
```

`[verified:src=NexusAgent/NexusAgent.Core/Agent/NexusOrchestrator.cs:41]` Per-call it creates a
fresh `ProofGoalGraph` (the Topos-backed per-run memory — see §5).
`[verified:src=NexusAgent/NexusAgent.Core/Agent/NexusOrchestrator.cs:82]`

`OrchestratorConfig` controls lifecycle: `MaxEpisodes` (def 100), `MaxTurnsPerEpisode` (def 20),
`EpisodeTimeout` (10 min), `OverallTimeout` (2 h), fossil thresholds, planner weights.
`[verified:src=NexusAgent/NexusAgent.Core/Agent/NexusOrchestrator.cs:390-410]`

### Layer 2 — `NexusProverSubagent` (the per-episode turn loop)

Each turn tries strategies in a strict tier order — cheapest first, escalate only on failure:

```
Tier 0.5  Graph replay     Topos ProofGoalGraph — has this exact goal succeeded before? Replay.
Tier 0.75 Graph-native     BestFirstGraphPlanner proposes a tactic from the in-process hypergraph.
Tier 1    Fossil vault     Query Neo4j :ProofFossil by goal-state vector similarity.
         (hallucination    HallucinationGate: fossil-vault corroboration, then majority-vote
          scan +           LLM classification. SUSPECT requires strict majority > 50%.
          cartographer
          hint)            ProofCartographer: dead-end detection (≥3 visits, ≥80% failure).
Tier 2-3  LLM router       TieredLlmRouter escalates: Tier1 (cheap) → Tier2 (DeepSeek Flash)
                          → Tier3 (Premium cloud). Hard USD budget cap, circuit breaker on
                          DeepSeek 402/401.
Compile   LeanOracle       Binary ground truth — the only authoritative signal.
Structural SketchValidator Rejects reward-hacking (substring matches, decl shadowing).
gate
Record    + fossilize      ProofCartographer records the transition; if progress,
                          ProofFossilizer persists the newly-proved subgoal to Neo4j.
```

`[verified:src=NexusAgent/NexusAgent.Core/Agent/NexusProverSubagent.cs:55 — RunEpisodeAsync]`
`[verified:src=NexusAgent/NexusAgent.Core/Agent/NexusProverSubagent.cs:108-543 — the tier order]`

### Layer 3 — Safety and memory subsystems

| Component | Purpose | Source |
|---|---|---|
| `HallucinationGate` | Two-layer: fossil-vault corroboration, then majority-vote LLM classification. `FossilCorroborationThreshold = 0.78f`. | `Safety/HallucinationGate.cs:22, 172` |
| `SketchValidator` | **`internal static`** — the anti-reward-hacking structural gate. `IsStructurallyValid(original, candidate)`. Namespace-aware. | `Agent/SketchValidator.cs:14, 40` |
| `ProofCartographer` | Records visited states (`:ProofLandmark`), detects dead-end regions. `MinVisitsForDeadEnd=3`, `DeadEndFractionThreshold=0.8`. | `Planning/ProofCartographer.cs:22, 131` |
| `TieredLlmRouter` | LLM escalation ladder. `BudgetCapUsd=200`, `TurnsBeforeEscalation=3`, latching circuit breaker on DeepSeek 402/401. | `Llm/TieredLlmRouter.cs:18, 99-115, 132` |
| `ProofFossilizer` | Writes/retrieves from the fossil vault. `static string RunId` (per-process). | `Memory/ProofFossilizer.cs:12, 22` |
| `BestFirstGraphPlanner` | The graph-native tactic proposer (Tier 0.75). Registered as a DI singleton. | `Planning/BestFirstGraphPlanner.cs`, `Cli/Program.cs:170` |

### The LLM tier ladder

`[verified:src=NexusAgent/NexusAgent.Core/Llm/ILlmClient.cs:32]` `[verified:src=NexusAgent/NexusAgent.Core/Models/ProofResult.cs:72]`

| Tier | Enum value | Purpose |
|---|---|---|
| `Tier0_FossilHit` | 0 | A fossil-vault direct hit — no LLM call at all. |
| `Tier0_5_GraphReplay` | 4 | A ProofGoalGraph replay — no LLM call. |
| `Tier0_GateJuror` | 5 | Used as a hallucination-gate juror. |
| `Tier1_Cheap` | 1 | The first LLM escalation (e.g. local Ollama Qwen). |
| `Tier2_DeepSeekFlash` | 2 | DeepSeek Flash (the workhorse for proof search). |
| `Tier3_PremiumCloud` | 3 | Gemini Pro / Claude / GPT-4-class (ceiling). |

Per-tier temperature override at SendAsync: Tier3=0.1, Tier2=0.3, Tier1=0.4.
`[verified:src=NexusAgent/NexusAgent.Core/Llm/TieredLlmRouter.cs:93-98]`

### What this improves (and doesn't) over a naive LLM-prover

Preserved from the older architecture stub, source-verified: `[verified:src=architecture.md:45-61]`

| Naive-LLM-prover failure mode | NexusVerifier mitigation |
|---|---|
| Sorry offloading into helper lemmas | `ProofCartographer` marks dead-end regions; prompt injection redirects |
| Hallucinated "established results" | `HallucinationGate`: sorry lemmas checked against fossil vault + Mathlib index |
| No cross-problem knowledge | Fossil vault persists to Neo4j — available to every future problem |
| $100–300/problem in cloud LLM cost | Tiered router: local DeepSeek first, premium cloud only at the ceiling |

**What this does NOT improve** (stated plainly):
- Raw LLM mathematical reasoning capability. DeepSeek R1 < Gemini 3.1 Pro at the ceiling; the graph
  compensates for search efficiency, not for mathematical imagination.
- Lean formalization skill. Still requires the user to provide a valid Lean sketch with `sorry`
  placeholders; the agent fills in proofs, it doesn't write the spec.
- Parallelism at cloud scale. Single-machine, graph-guided serial search by design.

---

## 4. Subsystem 3 — The Neo4j graph backend

**Schema file:** [`neo4j_schema.cypher`](neo4j_schema.cypher) (printed by `nexus schema`).

The persistent memory. Five node types:

| Label | Purpose | Key indexes |
|---|---|---|
| `:ProofFossil` | Proven sub-goals, the **fossil vault**. Indexed for similarity search. | unique `id`; vector index `proofFossils` (64-dim cosine) on `stateVector`; indexed `domainTag`, `useCount` |
| `:ProofLandmark` | Visited proof states (the topology the cartographer navigates). | unique `id`; vector index `proofLandmarks` (64-dim cosine); indexed `problemId` |
| `:MathProblem` | Problem registry. | unique `id` |
| `:LeanCompileCache` | The oracle's cache (sketch SHA-256 → result). | unique `sketchHash` |
| `:HyperedgeRecord` | Hypergraph edges from `scan-hg` Lean extraction. | unique `id`; indexed `outputHash` |

`[verified:src=docs/neo4j_schema.cypher]` Auto-ensured on every CLI startup except `probe`.
`[verified:src=NexusAgent/NexusAgent.Cli/Program.cs:184-189]`

> **The "fossil vault" is a concept, not a class.** No type named `FossilVault` exists. The vault
> = the `:ProofFossil` node store + its vector index, accessed through `ProofFossilizer` (read/
> write API in `NexusAgent.Core/Memory/`), `FossilSink` (write path during `ingest-parts`), and
> the Neo4j vector index. `[verified:src=NexusAgent/NexusAgent.Core/Memory/ProofFossilizer.cs:12]`
> `[verified:src=NexusAgent/NexusAgent.VerifiedParts/Sinks/FossilSink.cs:19]`

---

## 5. Subsystem 4 — The Topos integration (in-process, ephemeral)

**Full report:** [`TOPOS_INTEGRATION_REPORT.md`](TOPOS_INTEGRATION_REPORT.md)

Two *in-process* graphs (live for one `SolveAsync` run, **not** in Neo4j) backed by a Topos
`HypergraphKernel`:

### `ProofGoalGraph` — per-run goal/attempt memory

```csharp
public sealed class ProofGoalGraph
{
    Handle GetOrAddGoal(string goalText);                    // stable SHA-256 id
    void RecordAttempt(Guid beforeGoalId, string tacticText,
                       IReadOnlyList<Guid> afterGoalIds, AttemptOutcome outcome);
    IReadOnlyList<...> FailedAttemptsFor(Guid goalId);
    IReadOnlyList<Guid> SiblingsOf(Guid goalId);
    int DedupHits { get; }
}
public enum AttemptOutcome { Failed, CompiledNoProgress, Progressed }
```

`[verified:src=NexusAgent/NexusAgent.Core/Planning/ProofGoalGraph.cs:29, 40, 63, 100, 134, 157, 84, 200]`

Goal vertices and tactic-attempt edges are recorded as the prover explores, so the LLM prompt can
show failed-attempt history and sibling-subgoal structure for the goals it's currently working on,
instead of having zero memory of what already failed. **Purely additive prompt context** — never
influences compile/axiom verdicts. One instance per `SolveAsync` call, never a singleton.

`AttemptOutcome` distinguishes "Lean rejected this" (`Failed`) from "Lean accepted but no goal
closed" (`CompiledNoProgress`) from "real progress" (`Progressed`).

### The AND-OR backward chainer

The proof-search planner's candidate-lemma expansion is backed by a real `HypergraphKernel`
instance, not a hand-rolled linked structure. `[verified:src=README.md:140-143]` The full
integration report (what was built, the 12-test suite, Neo4j parity) is in
[`TOPOS_INTEGRATION_REPORT.md`](TOPOS_INTEGRATION_REPORT.md).

### Where the Lean-native hypergraph fits (the orthogonal line)

[`hypergraph-engine.md`](hypergraph-engine.md) documents a *different* hypergraph — a Lean-native
one that lives entirely inside the Lean elaborator (`_nexus_tmp/ErdosHypergraph.lean`), Phase 1
string-keyed with a Phase 2 Expr-hash roadmap. This is **not** the Topos integration. The two are
complementary research lines: the Lean-native engine extracts edges from Mathlib via elaborator
introspection; the Topos integration backs the runtime proof-search planner. They don't share code.
`[verified:docs=docs/hypergraph-engine.md]`

---

## 6. The verification gate sequence (`ingest-parts`)

End-to-end trace of what happens to one `VerifiedPart`:

```
VerifiedPart (statement + proof + imports + scope)
    │
    ▼ (a) Compile gate ─── ILeanOracle.CompileAsync(BuildProofSketch(part))
    │                     Wraps as `private noncomputable def _nexus_part_check : <stmt> := <proof>`
    │                     → Compiled bool, SorryCount int. Cached in :LeanCompileCache.
    │   REJECT if !Compiled || any error || SorryCount != 0
    │
    ▼ (b) Axiom gate ───── AxiomChecker.CheckAsync(imports, statement, proof)
    │                     Appends #print axioms, re-runs lean, parses axiom closure.
    │                     Returns null = compile errored; [] = no non-logical axioms.
    │   REJECT if sorryAx in closure, OR native escape under Reject policy
    │                     (ofReduceBool, native, trustCompiler)
    │
    ▼ (c) Scope guard ──── VerifiedPartIngestor
    │                     Scope==Full requires FullScopeConfirmed=true.
    │   REJECT otherwise
    │
    ▼ (d) Sink fan-out ─── BuildOpenGoalState → ProofState with PendingGoals=[StatementText]
       │
       ├─→ FossilSink.WriteAsync    → INeo4jClient.UpsertFossilAsync
       │                              → :ProofFossil node (64-dim stateVector, SourceProblems tag)
       │
       └─→ LandmarkSink.WriteAsync  → ProofCartographer.ObserveAsync (open + solved)
                                      + RecordTransitionAsync
                                      → :ProofLandmark nodes + :TRANSITION relationship
```

`[verified:src=NexusAgent/NexusAgent.VerifiedParts/VerifiedPartIngestor.cs:71-131]`
`[verified:src=NexusAgent/NexusAgent.VerifiedParts/AxiomChecker.cs:38-66, 125-130]`
`[verified:src=NexusAgent/NexusAgent.VerifiedParts/Sinks/FossilSink.cs:37]`
`[verified:src=NexusAgent/NexusAgent.VerifiedParts/Sinks/LandmarkSink.cs:37]`

### Holdout isolation

`--exclude-targets <path>` takes newline-separated declaration names. The exclusion is
**parent-problem-level**, computed by `ExtractParentProblemId` (first dot-segment, letters/digits
only, lowercased). Excluding `Erdos1074.erdos_1074.variants.EHSNumbers_init` excludes every
sibling under parent `erdos1074` — preventing leakage between related theorems from the same
benchmark file. `[verified:src=NexusAgent/NexusAgent.VerifiedParts/VerifiedPartsPlugin.cs:240]`
`[verified:src=NexusAgent/NexusAgent.Tests/VerifiedParts/ParentProblemIdTests.cs — 6 tests pinning this]`

### Axiom policy

`NEXUS_PARTS_NATIVE_DECIDE` controls step (b):

| Value | Behaviour | Verified |
|---|---|---|
| `reject` (default) | Parts using `native_decide` / `Lean.trustCompiler` are rejected | VerifiedPartIngestor.cs:94-96 |
| `flag` | Parts are accepted but tagged in `SourceProblems` (`:native-flagged`) | VerifiedPartIngestor.cs:59-62 |

> **Discrepancy:** the README documents `reject`/`warn`/`allow`; only `reject` and `flag` exist in
> code. Anything that isn't `"flag"` (including the README's `warn` and `.env.example`'s `allow`)
> falls through to `reject`. See [`CONFIGURATION.md`](CONFIGURATION.md).

---

## 7. What this architecture does *not* include

- **No cloud-scale parallelism.** Single-machine, graph-guided serial search by design.
- **No automated theorem-statement generation.** The agent requires a valid Lean sketch with
  `sorry` placeholders; it fills in proofs, it doesn't write the spec.
- **No improvement on raw LLM mathematical reasoning.** The graph compensates for search
  efficiency, not for mathematical imagination at the ceiling.
- **No cross-run persistence for the Topos graphs.** `ProofGoalGraph` and the chainer are
  ephemeral — one per `SolveAsync`. The fossil vault (Neo4j) is the cross-run memory; the Topos
  graphs are the within-run memory.
- **No novel proofs of open problems.** 0/23 against `FC100OpenSet1` is the capability claim.
  `[verified:src=README.md:62-72]`

---

## 8. The "LEGO piece" plugin boundary

`NexusAgent.VerifiedParts` is explicitly a removable layer over `NexusAgent.Core`. Source comments
give a 4-step removal recipe: `[verified:src=NexusAgent/NexusAgent.VerifiedParts/VerifiedPartsPlugin.cs:19-25]`
`[verified:src=NexusAgent/NexusAgent.Cli/Program.cs:22, 174]`

This means the verification mode (`ingest-parts`) is opt-in infrastructure — a consumer could use
just the agent pipeline (`NexusAgent.Core` + the orchestrator) without the verified-parts layer.
Useful to know if you're considering NexusVerifier as a library rather than a CLI.

---

## Cross-references

- **The Topos integration** (full report, the 12-test suite) → [`TOPOS_INTEGRATION_REPORT.md`](TOPOS_INTEGRATION_REPORT.md).
- **The Lean-native hypergraph** (the orthogonal Phase 1/2 line) → [`hypergraph-engine.md`](hypergraph-engine.md).
- **The benchmark correction** → [`FC100_GAP_SET_METHODOLOGY.md`](FC100_GAP_SET_METHODOLOGY.md).
- **Commands that drive this architecture** → [`CLI_REFERENCE.md`](CLI_REFERENCE.md).
- **Configuration knobs** → [`CONFIGURATION.md`](CONFIGURATION.md).
- **The Neo4j schema definition** → [`neo4j_schema.cypher`](neo4j_schema.cypher).
