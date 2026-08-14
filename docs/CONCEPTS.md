# Concepts — the NexusVerifier mental model

**Date:** 2026-07-27 · **Author:** GLM-5.2 (ZCode), under source-verification discipline · **Audience:**
anyone — new contributor, researcher evaluating the project, or user — who needs to *understand*
NexusVerifier before running it. This is the conceptual entry point; the runnable walkthrough is
[`GETTING_STARTED.md`](GETTING_STARTED.md), the command surface is
[`CLI_REFERENCE.md`](CLI_REFERENCE.md), and the system's internals are in
[`ARCHITECTURE.md`](ARCHITECTURE.md).

> **Provenance discipline.** Every non-trivial claim carries a tag — `[verified:src=path]` for
> claims read directly from NexusVerifier's source, `[verified:docs=path]` for claims read from
> another doc, `[verified:web=url]` for fetched sources, or `[unverified:inferred]` for inferences.
> Same standard as Topos's investigation docs. No unsourced assertions.

---

## What NexusVerifier is, in one paragraph

NexusVerifier is a **graph-augmented Lean 4 verification pipeline** for formally verified proof
artifacts. It does two distinct things, and the single most important conceptual point is that
**these are two separate modes**, not one pipeline: (1) **mechanical verification** of existing
Lean proof parts — compile via `lake env lean`, profile the axiom closure via `#print axioms`,
reject anything depending on native compilation, enforce declaration-family holdout isolation; and
(2) **active proof search** — an LLM-driven, graph-guided agent that attempts to produce
sorry-free Lean proofs of theorem statements from the [Formal Conjectures](https://github.com/google-deepmind/formal-conjectures)
benchmark corpus. Both modes share infrastructure (the Neo4j graph backend, the Lean oracle, the
Topos-backed in-process graphs), but they answer different questions and should not be conflated.
`[verified:src=NexusAgent/NexusAgent.Cli/Program.cs:194-206 — the `ingest-parts` and `bench`/`solve`
commands are distinct dispatch arms]`

> **Independence notice:** NexusVerifier is an independent research project by Nasser Towfigh. It
> is not affiliated with, endorsed by, or derived from Google DeepMind. It uses the Formal
> Conjectures benchmark released by Google DeepMind under Apache 2.0.
> `[verified:src=README.md:3-5]`

---

## The honest framing first — read this before anything else

NexusVerifier's documentation (this doc included) takes honesty as a load-bearing principle, not a
decorative one. Three things to internalize before forming expectations:

1. **NexusVerifier does not claim novel proofs of open mathematical problems.** Running the
   proof-search agent against the *actual* open-problems corpus (`FC100OpenSet1`) yielded
   **0 of 23 genuine open conjectures solved** — and that is described in the project's own README
   as *"the correct, expected outcome, not a failure."* `[verified:src=README.md:62-72]` This is
   the number that should be cited for the pipeline's actual proof-search capability, not the
   higher figures from earlier runs that turned out to be a corpus-selection error.
   `[verified:docs=docs/FC100_GAP_SET_METHODOLOGY.md — the corpus-error writeup]`

2. **Even the verification mode's "verified" results are not NexusVerifier's proofs.** When
   `ingest-parts` reports "7 verified parts," those are *pre-existing* Lean 4 proof terms authored
   by the FC benchmark contributors. NexusVerifier's role is mechanical verification (axiom-closure
   filtering + holdout isolation), not proof synthesis. `[verified:src=README.md:127-129]`

3. **The earlier benchmark numbers (7/10 sorry-free, 43/66 sorry-free) were real but
   mis-attributed.** They came from running against `FC100SolvedSet1` (already-solved problems)
   positioned as if they tested proof-search capability. The corrected rerun against the actual
   open-problems corpus is the 0/23 figure. Both numbers exist in the record; only the second is
   the capability claim. `[verified:docs=docs/FC100_GAP_SET_METHODOLOGY.md]`

This framing matters because the project's value isn't "solves open problems" — it's
**infrastructure that separates rigorous formal verification from superficially plausible proof
generation**, and the methodology that surfaced and corrected its own corpus error is itself part
of that value. `[verified:src=README.md:76-87]`

---

## The two modes

### Mode 1 — Mechanical verification (`ingest-parts`)

Given a JSON file of `VerifiedPart` records (each one a Lean statement + proof block + imports
header), `ingest-parts` runs each through a four-gate pipeline:

```
VerifiedPart (statement + proof + imports)
        │
        ▼
  ┌─────────────────────────────────────────┐
  │ (a) Compile gate                        │  ILeanOracle.CompileAsync
  │     lake env lean <sketch>              │  → Compiled bool, SorryCount int
  │     parse stdout/stderr                 │  → cached in Neo4j by SHA-256 of sketch
  └─────────────────────────────────────────┘
        │ pass (Compiled && SorryCount==0)
        ▼
  ┌─────────────────────────────────────────┐
  │ (b) Axiom gate                          │  AxiomChecker.CheckAsync
  │     append `#print axioms`, re-run lean │  → string[] axiom closure
  │     reject if sorryAx or native escape  │  (under NEXUS_PARTS_NATIVE_DECIDE=reject)
  └─────────────────────────────────────────┘
        │ pass (axiom closure ⊆ {propext, Classical.choice, Quot.sound})
        ▼
  ┌─────────────────────────────────────────┐
  │ (c) Scope guard                         │  VerifiedPartIngestor
  │     Scope==Full requires explicit       │
  │     FullScopeConfirmed flag             │
  └─────────────────────────────────────────┘
        │ pass
        ▼
  ┌─────────────────────────────────────────┐
  │ (d) Sink fan-out                        │  FossilSink, LandmarkSink
  │     → Neo4j :ProofFossil node           │  (the "fossil vault")
  │     → Neo4j :ProofLandmark + transition │  (visited-state topology)
  └─────────────────────────────────────────┘
```

`[verified:src=NexusAgent/NexusAgent.VerifiedParts/VerifiedPartIngestor.cs:71-131]`
`[verified:src=NexusAgent/NexusAgent.Core/Oracle/LeanOracle.cs:19]`
`[verified:src=NexusAgent/NexusAgent.VerifiedParts/AxiomChecker.cs:38-66]`

A proof is **Verified** when its axiom closure is a subset of `{propext, Classical.choice,
Quot.sound}` — the standard Lean 4 logical foundation. `[verified:src=README.md:308-311]` Any
additional axioms indicate reliance on unverified reduction paths (`decide +native`,
`Lean.ofReduceBool`, `Lean.trustCompiler`) and are rejected under the default `reject` policy.

**Holdout isolation** is a first-class concern: when one declaration from a problem file is held
out (via `--exclude-targets`), all sibling declarations from the same parent problem are excluded
automatically, preventing leakage between related theorems.
`[verified:src=NexusAgent/NexusAgent.VerifiedParts/VerifiedPartsPlugin.cs:240 — ExtractParentProblemId]`
`[verified:src=NexusAgent/NexusAgent.Tests/VerifiedParts/ParentProblemIdTests.cs — 6 tests pinning this behavior]`

### Mode 2 — Active proof search (`solve` / `bench`)

Given a theorem statement, the agent attempts to produce a sorry-free Lean 4 proof using its
graph-guided planner and tiered LLMs. The per-episode turn loop
(`NexusProverSubagent.RunEpisodeAsync`) is a *tiered* pipeline — each turn tries the cheapest
strategy that could make progress, escalating only when lower tiers fail:

```
Per turn, in this exact order:
  Tier 0.5  Graph replay    — has the Topos-backed ProofGoalGraph seen this exact
                              goal-state succeed before? Replay the recorded tactic.
  Tier 0.75 Graph-native    — propose a tactic from the in-process hypergraph
                              (the AND-OR backward chainer).
  Tier 1    Fossil vault    — query Neo4j :ProofFossil by goal-state vector similarity.
  (hallucination scan       — HallucinationGate checks the proposed sketch for
   + cartographer hint)       sorry-laundering and dead-end patterns.
  Tier 2-3  LLM router      — TieredLlmRouter escalates Tier1 (cheap) → Tier2
                              (DeepSeek Flash) → Tier3 (Premium cloud), with a
                              hard USD budget cap and a circuit breaker.
  Compile   LeanOracle      — binary ground truth; the only authoritative signal.
  Structural gate           — SketchValidator rejects reward-hacking (substring
                              name matches, shadowing the original decl with a def).
  Record    + fossilize     — ProofCartographer records the transition; if progress,
                              ProofFossilizer persists the newly-proved subgoal.
```

`[verified:src=NexusAgent/NexusAgent.Core/Agent/NexusProverSubagent.cs:55 — RunEpisodeAsync]`
`[verified:src=NexusAgent/NexusAgent.Core/Agent/NexusProverSubagent.cs:108-543 — the tier order]`

**`LeanOracle.Compile` is the only authoritative signal in the system.** Everything else — the
graph, the fossils, the LLM tiers, the cartographer — is *prompt context*. None of it influences
the `Compiled` / `SorryCount` / `IsFullyProved` verdicts, which come solely from running Lean
through the oracle. `[verified:src=NexusAgent/NexusAgent.Core/Oracle/ILeanOracle.cs:11-27 —
the interface doc literally says "the only authoritative signal in the system"]`

---

## The four subsystems

NexusVerifier has four major subsystems. Understanding what each owns is the key to not getting
lost in the codebase.

### 1. The Lean oracle — `NexusAgent.Core/Oracle/`

The binary ground-truth judge. Shells out to `lake env lean` on a temp file, parses the
diagnostics, and (since the 2026-07-25 sorry-citation-laundering fix) appends a `#print axioms`
probe to every theorem/lemma it declares — because citing an already-`sorry`'d declaration compiles
clean with `SorryCount=0` and no warning at the citing site; only `#print axioms` reveals the
inherited `sorryAx`. `[verified:src=NexusAgent/NexusAgent.Core/Oracle/LeanOracle.cs:113-122]`
Every compile result is cached in Neo4j keyed by SHA-256 of the sketch.
`[verified:src=NexusAgent/NexusAgent.Core/Oracle/LeanOracle.cs:37-50]`

### 2. The agent pipeline — `NexusAgent.Core/Agent/` + `Planning/` + `Safety/` + `Llm/`

The proof-search engine. Three layers worth knowing:

- **`NexusOrchestrator`** — manages the episode lifecycle for one problem, serially. Per-call it
  creates a fresh `ProofGoalGraph`. `[verified:src=NexusAgent/NexusAgent.Core/Agent/NexusOrchestrator.cs:41,82]`
- **`NexusProverSubagent`** — the per-episode turn loop (above).
  `[verified:src=NexusAgent/NexusAgent.Core/Agent/NexusProverSubagent.cs:55]`
- **`HallucinationGate`** — two-layer: fossil-vault corroboration, then majority-vote LLM
  classification. SUSPECT requires strict majority > 50%.
  `[verified:src=NexusAgent/NexusAgent.Core/Safety/HallucinationGate.cs:52-141]`
- **`TieredLlmRouter`** — escalation ladder with a hard budget cap and a circuit breaker that
  latches on DeepSeek 402/401 errors. `[verified:src=NexusAgent/NexusAgent.Core/Llm/TieredLlmRouter.cs:18-115]`
- **`ProofCartographer`** — records visited states and detects dead-end regions (a state visited
  ≥3 times with ≥80% failure rate). `[verified:src=NexusAgent/NexusAgent.Core/Planning/ProofCartographer.cs:22-81]`

### 3. The Neo4j graph backend — the fossil vault and landmark topology

Three node types live in Neo4j and form the persistent memory:

| Label | Purpose | Schema |
|---|---|---|
| `:ProofFossil` | Proven sub-goals, indexed for similarity search by 64-dim `stateVector` | vector index `proofFossils` (cosine), unique `id`, indexed `domainTag`/`useCount` |
| `:ProofLandmark` | Visited proof states (the topology the cartographer navigates) | vector index `proofLandmarks` (cosine), unique `id`, indexed `problemId` |
| `:MathProblem` | Problem registry | unique `id` |
| `:LeanCompileCache` | sketch SHA-256 → compile result (the oracle's cache) | unique `sketchHash` |
| `:HyperedgeRecord` | Hypergraph edges from `scan-hg` Lean extraction | unique `id`, indexed `outputHash` |

`[verified:src=docs/neo4j_schema.cypher]` `[verified:src=NexusAgent/NexusAgent.Cli/Program.cs:726-732 — the `schema` command prints this file]`

The "**fossil vault**" you'll see named throughout the codebase is the `:ProofFossil` store +
its vector index — the cross-problem memory of proven sub-goals. There's no class called
`FossilVault`; the concept is implemented by `ProofFossilizer` (write/retrieve), `FossilSink`
(verified-parts ingestion), and the Neo4j vector index together.

### 4. The Topos integration — in-process, ephemeral graphs

Topos (a typed-property hypergraph library, vendored as a git submodule at `external/Topos`) backs
two *in-process* graphs that live for one `SolveAsync` run, **not** in Neo4j:

- **The AND-OR backward chainer's candidate-lemma expansion** — backed by a real `HypergraphKernel`
  instance, not a hand-rolled linked structure. `[verified:src=README.md:140-143]`
- **`ProofGoalGraph`** — per-run memory of proof goals and tactic attempts. Goal vertices and
  tactic-attempt edges are recorded as the prover explores, so the LLM prompt can show
  failed-attempt history and sibling-subgoal structure for the goals it's currently working on,
  instead of having zero memory of what already failed.
  `[verified:src=NexusAgent/NexusAgent.Core/Planning/ProofGoalGraph.cs:29 — Topos HypergraphKernel at L40]`

Both are ephemeral (one instance per orchestrator call, never a singleton field
`[verified:src=NexusAgent/NexusAgent.Core/Planning/ProofGoalGraph.cs — one instance per SolveAsync]`)
and purely additive prompt context — they never influence the compile/axiom verdicts.

The full Topos integration story (what was built, what was tested, the 12-test suite that pins it)
is in [`docs/TOPOS_INTEGRATION_REPORT.md`](TOPOS_INTEGRATION_REPORT.md).

---

## What NexusVerifier is *not*

Scope boundaries, lifted from the existing docs and source:

- **Not a claim of novel proofs of open problems.** The 0/23 result against `FC100OpenSet1` is
  the capability claim. `[verified:src=README.md:32, 62-72]`
- **Not a proof *generator* in the verification mode.** `ingest-parts` mechanically verifies
  pre-existing proof artifacts; it doesn't synthesize them. `[verified:src=README.md:127-129]`
- **Not a substitute for raw LLM mathematical reasoning.** The graph compensates for search
  efficiency, not for mathematical imagination at the ceiling. `[verified:src=architecture.md:57-58]`
- **Not cloud-scale parallelism.** Single-machine, graph-guided serial search by design.
  `[verified:src=architecture.md:61]`
- **Not an improvement on Lean formalization skill.** The agent still requires a valid Lean sketch
  with `sorry` placeholders; it fills in proofs, it doesn't write the spec.
  `[verified:src=architecture.md:59-60]`

---

## The honesty discipline, made operational

NexusVerifier's documentation culture (which these docs extend, not replace) treats correctness
discipline as load-bearing. The 2026-07-26 pipeline-hardening cycle surfaced and fixed four real
false-positive/reward-hacking gaps `[verified:src=README.md:34-49]`, and a fifth issue — a
corpus-selection error — was caught, written up with full evidence, and corrected even though the
correction lowered the headline number. `[verified:docs=docs/FC100_GAP_SET_METHODOLOGY.md]` This
matters because the entire value proposition is *separating rigorous formal verification from
superficially plausible proof generation* — a project that papered over its own errors would be
useless for that purpose. The `[verified:...]` discipline these docs carry is the same principle
applied to documentation.

---

## Where to go next

- **Stand up a working environment** → [`GETTING_STARTED.md`](GETTING_STARTED.md). Real setup:
  clone with the Topos submodule, build a `formal-conjectures` checkout (~20 min for Mathlib),
  stand up Neo4j, configure `.env`, run a first dry-run.
- **Look up a command** → [`CLI_REFERENCE.md`](CLI_REFERENCE.md). All 8 commands (`solve`, `bench`,
  `schema`, `stats`, `probe`, `ingest-hg`, `scan-hg`, `ingest-parts`) with flags and outputs.
- **Look up a configuration knob** → [`CONFIGURATION.md`](CONFIGURATION.md). The real env-var
  surface (~19 vars, beyond the 8 the README lists) and three README-vs-code discrepancies worth
  knowing.
- **Understand the internals** → [`ARCHITECTURE.md`](ARCHITECTURE.md). The unified picture
  (Lean oracle + agent pipeline + Neo4j + Topos), replacing the older 61-line stub.
- **The Topos integration specifically** → [`TOPOS_INTEGRATION_REPORT.md`](TOPOS_INTEGRATION_REPORT.md).
- **The benchmark-corpus correction** → [`FC100_GAP_SET_METHODOLOGY.md`](FC100_GAP_SET_METHODOLOGY.md).
- **The Lean-native hypergraph (Phase 1, possibly orthogonal to Topos)** →
  [`hypergraph-engine.md`](hypergraph-engine.md).
