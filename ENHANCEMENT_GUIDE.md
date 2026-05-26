# Enhancement Guide

This document maps each component of the NexusAgent to its expected weak points
and the concrete upgrade path when you hit them. Read it once before starting,
and refer back when a metric isn't moving.

## How to use this guide

1. Run the OEIS benchmark on 5–10 problems with the default configuration.
2. Look at which metric is underperforming (low fossil hit rate? high cost?
   low solve rate?).
3. Find the matching section below and apply the suggested enhancement.
4. Re-run the same 5–10 problems and compare.

Treat the defaults as a starting point, not a target.

---

## 1. ProofStateEncoder — when fossil matching is too coarse

**Symptom:** Fossil hit rate stays below 10% even after 30 problems, and
manual inspection shows the cached tactic blocks would have worked for the
unsolved problems if the encoder had recognized the similarity.

**Diagnosis:**
```bash
# Run with debug logging and look for these lines:
nexus solve problem.lean --id X
# "Fossil retrieval: 0 matches" repeatedly = encoder is too coarse
```

**Upgrade paths (in order of effort):**

### 1a. Tune the slot weights (minutes)

The default encoder distributes 64 dimensions equally across 6 slots. If
problems in your benchmark cluster by goal shape (slot 16–31) more than by
tactic history (slot 0–15), increase the goal-shape allocation. Modify
`ProofStateEncoder.cs`:

```csharp
// Reallocate: 8 slots for tactic bag, 24 for goal shape
EncodeTacticBag(state, v.AsSpan(0, 8));
EncodeGoalShape(state, v.AsSpan(8, 24));
EncodeHypothesisFingerprint(state, v.AsSpan(32, 16));
// ... rest unchanged
```

### 1b. Expand the tactic vocab (hours)

The default vocab has ~150 tactics. Mathlib has roughly 1000. Scan Mathlib
source for the top-500 most-cited tactic names:

```bash
cd ~/.elan/toolchains/*/lib/lean4/library
grep -rhoE '\b(rw|simp|apply|exact|rcases|obtain|have|...)\b' . | sort | uniq -c | sort -rn > /tmp/tactics.txt
```

Replace `data/tactics_vocab.json` with the top 500. Larger vocab → finer
bucketing → better fossil discrimination.

### 1c. Swap to a neural embedding (days)

When the deterministic encoder hits a ceiling, replace it with a 512-dim
embedding from a local model:

```bash
ollama pull nomic-embed-text
```

Create a new class `NomicEmbedEncoder` implementing the same shape
(`Encode(ProofState) → float[]`), update the Neo4j vector index dimension
from 64 to 512, and update DI registration. The interface stays the same;
this is a one-line change at the registration site.

**Trade-off:** higher quality matching, ~50ms per encode vs. ~1ms now, and
loss of interpretability (you can't inspect which dimension means what).

---

## 2. HallucinationGate — when warnings are unreliable

**Symptom (false positives):** Real Mathlib theorems are flagged as
suspect, slowing the prover by ~1 extra turn per false positive.

**Diagnosis:** Log every classification verdict for one benchmark run.
False positives appear as `SUSPECT` verdicts on lemma names you can verify
exist in Mathlib (e.g., `Nat.succ_pred_eq_of_pos`).

**Fix:**
- Add an exact-match check against the Mathlib name index before invoking
  the Qwen classifier. The Mathlib API name list is downloadable as a flat
  text file from the Mathlib repo. Store it as a `HashSet<string>` in
  `HallucinationGate` and short-circuit on exact match.

**Symptom (false negatives):** The prover keeps producing fake lemmas and
the gate misses them. Solve rate stays low; manual proof inspection shows
hallucinated lemmas in the failures.

**Fix:**
- Raise `FossilCorroborationThreshold` from 0.78 to 0.85 (fewer false
  corroborations).
- Change the classifier prompt to require justification: "If REAL, name the
  Mathlib namespace this theorem lives in. If SUSPECT, output SUSPECT only."
  Then parse: any response without a recognizable Mathlib namespace prefix
  gets reclassified as SUSPECT.

---

## 3. ProofCartographer — when dead-end detection misses

**Symptom:** The same proof state is visited 10+ times across episodes with
no progress, and the dead-end hint never fires.

**Diagnosis:**
```cypher
// Run in Neo4j browser:
MATCH (l:ProofLandmark)
WHERE l.visitCount >= 5
RETURN l.problemId, l.visitCount, l.deadEndCount, l.bestOutcome
ORDER BY l.visitCount DESC LIMIT 20
```

If `deadEndCount` is high but the prover keeps revisiting, the
`NearMatchThreshold` is too high. Try 0.85 instead of 0.92.

**Symptom:** Hint fires too aggressively, steering away from viable
approaches.

**Fix:** Raise `MinVisitsForDeadEnd` from 3 to 5. Or raise
`DeadEndFractionThreshold` from 0.8 to 0.95 (only steer away if nearly all
attempts failed).

---

## 4. LeanOracle — when compilation is the bottleneck

**Symptom:** Each turn takes 15+ seconds; total benchmark wall-clock is
dominated by Lean compile time.

**Upgrade paths:**

### 4a. Use Lean's language-server mode (recommended)

Replace the `lake env lean tempfile.lean` shell-out with a persistent
language server process that holds Mathlib in memory and accepts incremental
compile requests over stdio. Lean 4 ships `--server` mode for exactly this.

In `LeanOracle.cs`, replace the `Process.Start` block with a long-running
`Lean.Lsp` client. This eliminates Mathlib reload on every turn (saves
~5–10s per call after warm-up).

### 4b. Parallelise the compile cache lookup

Right now the cache check is sequential with compile. Run them concurrently:
fire the Neo4j lookup and the Lean compile at the same time, take the cache
result if it returns first, cancel the compile.

### 4c. Pre-compile common subgoal libraries

For problems in the same domain (e.g., 50 OEIS combinatorics problems), all
share Mathlib imports. Build a single `lake build` cache once at startup;
all subsequent compiles reuse it.

---

## 5. TieredLlmRouter — when budget is wasted

**Symptom:** Spending hits 80%+ of budget but solve rate is below 20%.

**Diagnosis:** Look at the per-tier call distribution in the benchmark
summary. If `Pro` calls dominate but solve rate is low, the router is
escalating too aggressively.

**Tuning levers (in `RouterConfig`):**
- Raise `TurnsBeforeEscalation` from 3 to 5 — give Qwen more time before
  escalating to Flash.
- Raise `TurnsBeforeFlashEscalation` from 4 to 6 — give Flash more time
  before going to Pro.
- Raise `EpisodesBeforeProEscalation` from 20 to 40 — only spend Pro budget
  on problems the early episodes couldn't crack.

**Aggressive cost mode:** Set all three escalation thresholds high enough
that Pro is only used as a last resort. Expected: 30–50% lower spend, 10–20%
fewer solves.

---

## 6. Fossil quality — when reuse exists but doesn't help

**Symptom:** Fossil hit rate is high (30%+), but the substituted tactic
blocks fail to compile.

**Diagnosis:** The fossils were proved in slightly different contexts
(different imports, different hypothesis names) and don't drop in cleanly.

**Fix paths:**

### 6a. Store import context with fossils

Extend `ProofFossil` schema with a `RequiredImports` field. When retrieving,
filter to fossils whose imports are a subset of the current sketch's imports.

### 6b. Run a "compile-as-substitution" check before injecting

Currently, the subagent substitutes a fossil directly and lets the next
compile reveal the failure. Instead, run a quick `CheckSubgoalAsync` first
— if it fails, don't substitute, fall back to LLM generation.

### 6c. Two-tier fossil retrieval

Stage 1: vector similarity over `ProofState` (current).
Stage 2: re-rank top-10 candidates by exact hypothesis-name overlap with
the current proof state. Production: implement in `ProofFossilizer.FindCandidatesAsync`.

---

## 7. Integration with Rich-Learning-Base V2

The local interfaces in `NexusAgent.Core` (specifically `INeo4jClient`,
`ProofFossilizer`, `ProofCartographer`) intentionally mirror the V2
abstractions. The minimal change to integrate:

1. Uncomment the `<ProjectReference>` in `NexusAgent.Core.csproj`.
2. Replace `Neo4jClient` with `RichLearning.V2.Memory.Neo4jGraphMemory`
   (matching shape).
3. Wrap `ProofFossilizer` to delegate to `DapsaEngine<ProofRequest, string>`
   instead of writing directly to Neo4j. The DapsaEngine call provides the
   full DAPSA episode: PERCEIVE → QUERY S^P → CONSONANCE → QUERY S^A →
   LEARN → FOSSILIZE.
4. Replace the local `IConsonanceChecker`-equivalent in `HallucinationGate`
   with `RichLearning.V2.Abstractions.IConsonanceChecker<ProofRequest>`.
5. Replace `ProofStateEncoder` with a class that implements
   `RichLearning.V2.Abstractions.IStateEncoder` (output dimension changes
   from 64 to whatever V2 uses; update the Neo4j vector index accordingly).

After this swap, the project becomes a strict superset of FSDE's pattern —
NexusAgent.Core sits on top of `RichLearning.V2` exactly the way
`Fsde.Engine` does, just specialized to Lean theorem proving instead of C#
code analysis.

---

## 8. Adding a new benchmark source

The CLI currently supports OEIS and Erdős. To add a new source (e.g.,
Putnam, IMO, Ben Green's list):

1. Place Lean files in `NexusLean/Problems/<Source>/`.
2. Run: `nexus bench ./NexusLean/Problems/<Source> --source <Source>`.

No code changes required — the CLI reads `.lean` files and uses filename as
problem ID. To customise per-source behaviour (e.g., different domain tags,
different episode budgets), extend `RunBenchAsync` in `Program.cs`.

---

## 9. Monitoring during a long run

While the benchmark runs, open the Neo4j Browser (http://localhost:7474)
and use these queries to watch progress in real time:

```cypher
// Fossil vault growth
MATCH (f:ProofFossil) RETURN count(f) AS fossilCount;

// Top-reused fossils
MATCH (f:ProofFossil) WHERE coalesce(f.useCount, 0) > 0
RETURN f.subgoalText, f.useCount ORDER BY f.useCount DESC LIMIT 10;

// Solved problems
MATCH (p:MathProblem {status: 'Solved'})
RETURN p.id, p.episodesUsed, p.solvedAt ORDER BY p.solvedAt DESC;

// Dead-end concentration (where the agent is stuck)
MATCH (l:ProofLandmark)
WHERE l.visitCount >= 5 AND toFloat(l.deadEndCount) / l.visitCount > 0.7
RETURN l.problemId, l.visitCount, l.deadEndCount
ORDER BY l.visitCount DESC LIMIT 10;
```

---

## 10. Known shortcuts in this skeleton — to revisit

The following are intentional simplifications. Each is documented inline
where it occurs:

| Location | Shortcut | When to fix |
|---|---|---|
| `LeanOracle.ParseLeanOutput` | Regex-based parsing of Lean output | When parsing precision becomes a bottleneck; use Lean LSP |
| `NexusProverSubagent.ExtractHypotheses` | Naive line-based hypothesis extraction | Same — use Lean LSP for proper goal/hypothesis state |
| `HallucinationGate.LemmaPattern` | Regex Lean parser | Replace with tree-sitter-lean once stability matters |
| `ProofStateEncoder` | Deterministic dim-64 encoding | See §1c above |
| `ProofFossilizer.FossilizeAsync` | Records best-effort tactic diff | Replace with proper sub-proof extraction via Lean LSP |
| `NexusOrchestrator.RunBenchAsync` | Serial problem execution | Acceptable — see SPEC.md §6 for rationale |
