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

## 2.5 N-ary chainer rewrite — Topos-native, no V2 projection

After the faithful-port milestone landed, the deferred "n-ary chainer rewrite" was done. The
chainer now reads Topos's **genuine n-ary shape directly** — no V2 `HyperEdge` projection, no
`IGraphMemory`, no Anchor==Target contortion. A second chain path (`--chain nary`, the default)
joins the projected path (`--chain projected`) so both can be compared apples-to-apples on the
same data.

**What changed:**
- `NarySearch/NaryBackwardChainer.cs` — reads `GetVertexHyperedges(goal)` → `IncidencesFrom(edge)`
  → filter by role byte. The AND-branch is the edge's after-role members, as many as the tactic
  produced. No projection.
- `NarySearch/ToposNativeCreditAssignment.cs` — reinforces a Topos `LearnableEdge` property via
  `GetProperty → LearnableEdge.Reinforce → SetProperty`. Pure kernel, no `HyperEdge` mutation.
- `NarySearch/NaryTrainingLoop.cs` + `NaryEvalHarness.cs` — mirror the projected versions but over
  the kernel directly.
- `ToposAppliesAdapter.BuildNaryAsync` — returns the kernel + goalHash→Handle map (no
  `ToposGraphMemory`, no V2 projection).
- `Program.cs --chain nary|projected` dispatches between the two paths.

**Correctness parity (the load-bearing result):** on the synthetic fixture, the n-ary and
projected chainers produce **identical** solve outcomes:

| goal | arm | n-ary | projected |
|---|---|---|---|
| T2_root | BaselineN | solved, 3 steps, 3 fired | solved, 3 steps, 3 fired |
| T2_root | BaselineH | solved, 3 steps, 3 fired | solved, 3 steps, 3 fired |
| T2_root | Learned | solved, 3 steps, 3 fired | solved, 3 steps, 3 fired |

Pinned by `NaryProjectedParityTests` (`[Theory]` over T1 and T2). The n-ary rewrite is a true
structural improvement with **no behavioral regression** — same search results, no projection
layer, no V2 cardinality contortion, no reference-stability contract.

**10 new tests pass** (22 total): n-ary ingest count, T1/T2 solve matching projected, junction read
natively, leaf/timeout, LearnableEdge reinforcement round-trip, n-ary L9 eval-freeze canary,
2 cross-path parity tests.

### Issue #2 (reference stability) is dissolved by the n-ary path

The faithful-port path's hardest contract — backends must return the same `HyperEdge` instance per
id or credit assignment silently breaks — **does not exist in the n-ary path**. There's no shared
mutable object to keep in sync: `LearnableEdge` is an immutable value type, and the kernel is the
single source of truth. Read-modify-write goes through the kernel every time
(`GetProperty → Reinforce → SetProperty`), so "the current theta" is always whatever's in the
kernel. Issue #2 remains a real finding about the V2 `IGraphMemory` contract (recorded below for
consumers who stay on the projected path), but the n-ary path demonstrates the cleaner pattern:
immutable values + kernel-as-source-of-truth eliminates the whole class of silent-desync bugs.

### Feature-vector parity (a deliberate simplification, not a regression)

The projected path uses V2's 7-slot `HyperEdge.ThetaParameters` (bias + X0 + X1 + 3 statistic
slots + 1 structural discriminator). The n-ary path uses a 3-slot `LearnableEdge` (bias + X0 + X1).
Same two decision-context features (`X0 = tanh(depth/10)`, `X1 = cosine-sim(current, root)`), same
gradient-ascent math, deliberately fewer feature slots. Reason: the goal was to prove n-ary
*storage and traversal* work, not to reproduce V2's exact theta representation. This is why
theta-coverage numbers differ between paths (n-ary: 14.29% trained; projected: 28.57%) — different
theta shapes, not different learning quality. A future milestone that wants strict theta parity
could extend `LearnableEdge` to 7 features; the kernel API supports it without change.

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
**Severity: medium-high (silent failure mode). Resolved for the n-ary path — see §2.5.**

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

**Resolution (n-ary path):** the n-ary chainer rewrite (§2.5) eliminates this entire class of bug
by using Topos's immutable `LearnableEdge` + the kernel as single source of truth — read-modify-
write goes through the kernel every time, so there's no shared mutable object to desync. Issue #2
remains valid for any consumer that stays on the projected path / V2 `IGraphMemory`, but the n-ary
path is the recommended pattern going forward.

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

# N-ary chain (default — Topos-native, no V2 projection):
dotnet run -- --fixture synthetic                                # zero-dependency structural run
dotnet run -- --fixture synthetic --chain nary --episodes 100    # bigger n-ary run

# Projected chain (the faithful-port baseline — Topos storage, V2 boundary):
dotnet run -- --fixture synthetic --chain projected

# Live Neo4j (requires shared Desktop instance + NEXUS_NEO4J_PASSWORD):
dotnet run -- --fixture neo4j --chain nary

# Tests:
cd ../NexusAgent.ToposExperiment.Tests
dotnet test --filter "FullyQualifiedName!~Neo4jParity"   # 22 tests, ~33ms, no external deps
```

## 7. Status

**Done, including the n-ary rewrite.** Topos is proven as a working storage backend for a second
non-RLB domain, purely through its public API, with no Topos-side changes. The chainer now reads
Topos's genuine n-ary shape directly (no V2 projection), producing identical search outcomes to
the projected path on the same data — the structural improvement with zero behavioral regression.
22 tests pin the load-bearing contracts across both chain paths. Six findings recorded for Topos's
M8 review, of which issue #2 (reference stability) is dissolved by the n-ary path's immutable-value
+ kernel-as-source-of-truth pattern.
