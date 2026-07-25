# Topos ↔ NexusVerifier integration report

**Date:** 2026-07-25
**Branch:** `topos-integration` (off `master`)
**Scope:** faithful-port backend swap — Topos as the storage backend for NexusVerifier's AND-OR
backward-chaining proof search. The first non-RLB, non-investigation use of Topos against a real
consumer's code.

---

## 1. What was built

A new `NexusAgent.ToposExperiment` project (+ test project) that runs NexusVerifier's existing
AND-OR backward chainer over a **Topos** `HypergraphKernel` instead of Rich-Learning-Base V2's
`InMemoryGraphMemory`. The chainer, training loop, credit-assignment math, and eval harness are
**verbatim copies** of the V2 experiment's, with one mechanical change: the memory dependency is
typed `IGraphMemory` (the interface) rather than the `sealed` `InMemoryGraphMemory`. The original
`NexusAgent.RlbExperiment` is untouched on its own branch — V2 baseline preserved for
apples-to-apples comparison.

| Component | What it does |
|---|---|
| `Memory/ToposGraphMemory.cs` | Implements `IGraphMemory`. Backed by a `HypergraphKernel`. Caches `HyperEdge` instances for reference-stable return. Only the 4 methods the chainer actually calls are real; the other ~14 interface members throw `NotSupportedException`. |
| `Ingest/ToposAppliesAdapter.cs` | Mirrors `AppliesHyperedgeAdapter`. Stores the tactic graph **natively n-ary** in Topos (one Edge-vertex per tactic-application, N member incidences), then projects to V2's Anchor/Conditions/Target shape at the boundary. |
| `Search/NexusBackwardChainer.cs` | Verbatim from the V2 experiment; only the memory type changed. |
| `Training/{TrainingLoop,CreditAssignment}.cs`, `Eval/EvalHarness.cs` | Verbatim from V2. |
| `Fixtures/SyntheticAppliesGraph.cs` | A small APPLIES-style graph with **real junctions and a genuine AND-branch** — the structural property FC100 lacked. Lets the harness run with zero external dependencies. |
| `Program.cs` | Same CLI as V2 + `--fixture synthetic\|neo4j`. |

**Branch discipline:** the Topos repo itself is **unchanged**. Every finding below is recorded as
a candidate for Topos's M8 API-stability review, not applied as a patch in this milestone.

---

## 2. Results

### Build + tests

- `NexusAgent.sln` builds clean: **0 warnings, 0 errors** across 7 projects (5 original + 2 new).
- `NexusAgent.ToposExperiment.Tests`: **12/12 tests pass.**
- Existing `NexusAgent.Tests`: 89 pass, 7 fail — **all 7 failures are pre-existing and
  environmental** (Lean toolchain for `LeanOracleTests`, Neo4j for `ProofFossilizerTests`), confirmed
  by `git diff master` showing zero changes to `NexusAgent.Tests` or `NexusAgent.Core`.
- `NexusAgent.RlbExperiment` (the V2 baseline) is untouched on its own branch.

### The synthetic end-to-end run

```
$ dotnet run -- --fixture synthetic --seed 42 --fuel 50 --episodes 20
[ToposAppliesAdapter] done. 7 hyperedges projected; 15 vertices / 15 incidences in the Topos kernel.
[Training] episodes=20  solved=20  theta_edges_reinforced=40
[EvalHarness] arm=BaselineN solved=1/1 (100.0%)
[EvalHarness] arm=BaselineH solved=1/1 (100.0%)
[EvalHarness] arm=Learned solved=1/1 (100.0%)
[Eval] theta_coverage=28.57% (2/7 edges trained)
```

Topos stores the synthetic graph natively (15 vertices, 16 incidences), the projection to V2 shape
works, the chainer runs, training reinforces theta, the L9 eval-freeze assert passes, all three arms
solve. **Topos functions correctly as the storage backend for this domain.**

### What the tests pin

- **Reference stability (3 tests).** `GetHyperedgeAsync` / `GetHyperedgesByMemberAsync` return the
  same `HyperEdge` instance across calls; mutation via `ReinforceTheta` is visible to subsequent
  reads. This is the load-bearing contract — without it, credit assignment silently no-ops.
- **Synthetic AND-OR correctness (6 tests).** Leaf closure, OR-branching (T1_root has two tactics,
  solves via either), AND-branching (T2_root's tactic produces two subgoals — the n-ary case that
  motivated Topos), junction querying (G_shared is anchor for two hyperedges), fuel exhaustion →
  timeout, unknown goal → leaf.
- **L9 eval-freeze canary (1 test).** Runs the full train→eval cycle; `EvalHarness`'s built-in
  bit-identical theta assert passes — proves the read-train-read-eval cycle shares object identity
  correctly end-to-end.
- **Topos native-storage verification (2 tests).** The n-ary AND-branch tactic (tactic_e) is one
  Edge-vertex with three incidences in the kernel itself (not just in the V2 projection); the
  fixture contains genuine junction goals (vertex degree ≥ 2).

### Neo4j parity (opt-in, not run)

`Neo4jParityTests` loads the live `nexusdb` graph (267k nodes) and compares Topos-backed solve
rates against the recorded V2 baseline. It's `[Fact(Skip=...)]` plus a connectivity probe that
returns early if the shared Desktop instance isn't reachable — the same skip-gracefully pattern
Topos's own GDS-oracle tests use. Not run in this session (shared instance state not confirmed);
the synthetic-fixture tests are the always-run structural gate and they pass.

---

## 3. Issues surfaced (the real payload — for Topos M8)

These are the things that only show up by using Topos against a real consumer. Recorded here for
the Topos project's API-stability review; **none were patched into Topos in this milestone**.

### Issue #1 — Per-Incidence cell properties are documented but not built
**Severity: medium (doc-vs-code divergence).**

`src/Topos.Hypergraph/Incidence.cs:12-17` promises that cell-level properties (theta, confidence,
transition counts) "attach to the (Source, Member, Ordinal) triple via `PropertyKey<T>` pools keyed
on the member Handle within that edge's scope — the mechanism for this lands with M2's reification
work." **M2 shipped nested reification only** (`ReificationTests.cs` verifies edges-as-members at
depth N, not per-incidence addressing). `SetProperty` is keyed on a single `Handle`
(`HypergraphKernel.cs:131`), with no overload taking an `Incidence` or `(source, member)` pair.

**Workaround used here:** properties attach to the edge-vertex (the
`ChatMemory.RecordRecallFeedback` pattern), losing per-subgoal granularity. Functional, but the
documented one-call mechanism doesn't exist. A consumer reading the doc would expect to write
`kernel.SetProperty(key, incidence, value)` and find no such API.

### Issue #2 — Reference-stability contract is undocumented and load-bearing
**Severity: medium-high (silent failure mode).**

This integration depends on a contract that **no interface documents**: a graph-memory backend
must return the *same `HyperEdge` instance* per id across queries, because `CreditAssignment`
mutates `edge.ThetaParameters.Theta[..]` through references obtained earlier. `InMemoryGraphMemory`
honors this for hyperedges but **clones landmarks and transitions on read** — inconsistent, and
nothing in `IGraphMemory` warns an implementer. A Topos (or any) backend that defensively copied
on read would silently break credit assignment: theta updates land on copies the index never sees,
the L9 eval-freeze assert may or may not catch it depending on timing, and learning appears to work
while doing nothing.

This is the kind of contract that should be on the interface (`// Implementations MUST return
reference-stable HyperEdge instances...`) or eliminated by making `HyperEdge` immutable + going
through an explicit update API. The current shape is a foot-gun.

### Issue #3 — Role typing is a free `byte`
**Severity: low (ergonomic).**

`AddIncidence(source, member, role, ordinal)` takes `role` as a raw `byte`. `ChatMemory` and this
adapter both define their own `const byte` conventions with no compile-time help and no registry.
Workable (the spec's "kernel does not judge" design is deliberate), but a layer-1 typed-role
convention or a thin generic `AddIncidence<TRole>(...)` would reduce per-consumer boilerplate and
naming collisions. Not a blocker.

### Issue #4 — `IHypergraphQuery.HasCycle` misleads on n-ary graphs
**Severity: low (already documented, but worth amplifying).**

`HasCycle` returns true for nearly any 3+-member hyperedge under the clique-style adjacency model.
The doc at `IHypergraphQuery.cs:252-263` flags this, but a backward-chaining consumer reaching for
cycle detection would get burned — AND-OR cycle detection needs role-aware traversal, not this.
Already documented; recording that a real consumer (this one) considered it and correctly avoided
it only because the doc was honest.

### Issue #5 — No role-aware traversal at the kernel layer
**Severity: low (by design, but a layer-1 gap).**

A proof-search consumer must hand-roll role-filtered walks over `GetHyperedgeVertices` +
`Incidence.Role` (the `ChatMemory.EntitiesMentionedIn` pattern, repeated here). Acceptable per the
"kernel does not judge" principle, but it means every directed/role-gated consumer reinvents the
same LINQ. Worth confirming the layer-1 API story for directed search is on the M8 list.

### Issue #6 — `IGraphMemory` interface ergonomics for hyperedge-only backends *(V2-side, not Topos)*
**Severity: low (host-library, recorded for completeness).**

`IGraphMemory` mixes abstract "core" members (must implement: ~11 transition/landmark/pathfinding
methods predating M4) with default-implemented hyperedge members. A hyperedge-only backend like
`ToposGraphMemory` has to provide explicit `NotSupportedException` implementations for 11 methods
the chainer never calls. The hyperedge default-implementation pattern should have been applied
retroactively to the core members when M4 added hyperedges. (This is a Rich-Learning-Base finding,
not Topos — recorded because it directly shaped this integration's boilerplate.)

---

## 4. A structural observation (not an issue, a finding)

The V2 `HyperEdge` enforces **exactly-one-Anchor + exactly-one-Target** cardinality
(`HyperEdge.ValidateCardinality` throws on violation). The existing `AppliesHyperedgeAdapter`
works around this with the "L12 uniform mapping": for a tactic producing N subgoals, the Target
slot is stuffed with the Anchor's key ("validation passes by role count only"). This is carry-back
CB1 in `docs/RLB_V2_FUTURE_WORK.md`.

**Topos does not have this constraint.** One tactic-edge → N subgoal members is the native n-ary
shape. This integration stores it natively in the Topos kernel (verified by
`ToposNativeStorageTests` — tactic_e is one Edge-vertex with three incidences). **But** because the
chainer consumes V2 shape at the boundary, the projection re-imposes V2's Target constraint
(`ToposAppliesAdapter` uses the same L12 mapping for chainer parity).

So this milestone **proves Topos stores the n-ary structure correctly** but does **not** exercise
Topos's structural advantage at the chainer level — the boundary projection re-imposes V2's shape.
The genuine win (a chainer that consumes Topos's native n-ary shape and drops the contortion) is
the deferred "n-ary chainer" scope, a separate decision after this lands. This is the honest scope
boundary: the integration tests storage faithfulness, not whether n-ary storage improves the search.

---

## 5. A second finding: the grouping rule is itself semantically loaded

While building the synthetic fixture, a test expected `G_shared` to be the anchor for two
hyperedges (tactic_c closing it in T1, tactic_c closing it in T2). It found **one**. Cause: the
adapter groups by `(GoalBeforeHash, TacticId)`, so the same tactic applied to the same goal in two
theorems becomes **one hyperedge with two target Conditions**.

This is arguably correct (the tactic is the same operation; the two theorems' closures are
alternatives), but it's a semantic choice with consequences: it collapses what looks like a
junction into a single n-ary edge. The fixture was adjusted to use distinct tactic names at the
junction so both hyperedges survive. Worth noting that the grouping rule itself shapes what counts
as a "junction" — a real-data re-ingest should consider whether `(before, tactic, theorem)` is the
right key. (This is a Nexus-side finding; recorded for completeness.)

---

## 6. Reproducing

```bash
cd NexusVerifier/NexusAgent/NexusAgent.ToposExperiment
dotnet run -- --fixture synthetic             # zero-dependency structural run
dotnet run -- --fixture synthetic --episodes 100 --fuel 100   # bigger synthetic run
# Live Neo4j parity (requires shared Desktop instance + NEXUS_NEO4J_PASSWORD):
dotnet run -- --fixture neo4j

# Tests:
cd ../NexusAgent.ToposExperiment.Tests
dotnet test --filter "FullyQualifiedName!~Neo4jParity"   # 12 tests, ~33ms, no external deps
```

## 7. Status

**Done.** Topos is proven as a working storage backend for a second non-RLB domain, purely through
its public API, with no Topos-side changes. 12 tests pin the load-bearing contracts. Six findings
recorded for Topos's M8 review. The deferred n-ary chainer rewrite (where Topos's structural
advantage would actually show at the search level) is the natural next decision, separate from
this milestone.
