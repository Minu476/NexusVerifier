# Pivot-gate A/B (round 2) — null result

**Date:** 2026-08-04 · **Status:** stopped deliberately at 3 of 6 planned runs; result was already
interpretable. · **Supersedes the interpretation in** `PIVOT_AB_2026-08-03.md` (round 1) and
corrects two claims made in the Stage 1 report (see §"Corrections").

**Headline: the pivot gates produced no additional independent solves. Control and treatment each
produced exactly one — the same problem, via a byte-identical proof.**

---

## What this round changed vs. round 1

Round 1 (2026-08-03) found 0 independent solves in both arms and was judged uninterpretable for
two reasons. Both were addressed before this round:

1. **Reformulation retry was a blind resample** (`bcd1f4d`) — the log claimed "retrying with error
   feedback" but sent an identical prompt. Now genuinely feeds the compile error back.
2. **The citation gate was a *reporting* gate, not a *search* gate.** It reclassified a citation
   after the fact and then returned immediately, so on citation-prone problems the episode ended
   before any stuck condition and the pivot machinery never ran at all. Now a detected citation is
   treated as not-solved and **search continues**, forcing those problems into the stuck-episode
   path that triggers reformulation.

A third fix was procedural: round 1's `Solved` flags persist in Neo4j and `Program.cs`'s
already-solved check would have silently skipped 12 of 34 problem-slots on a rerun. Fresh source
tags (`Stage1Treat`, `AB2C1`, `AB2T1`) avoid it.

Note that change 2 is **not** gated behind `PivotGatesEnabled` — it applies to both arms, which is
correct: it is a metric-integrity fix, not a pivot feature. That symmetry matters for reading the
result below.

---

## Results (3 completed runs, 17 problems each)

| Run | Arm | Independent solves | Citations detected | Turns | Struct. rejects | Cost |
|---|---|---|---|---|---|---|
| `Stage1Treat` | gates ON | **1** | 8 | 295 | 0 | $0.578 |
| `AB2C1` | gates OFF (control) | **1** | 5 | 277 | 6 | $0.389 |
| `AB2T1` | gates ON (treatment) | **1** | 8 | 267 | 0 | $0.483 |

The single independent solve is `tripleProduct_const` in **all three runs**:

```lean
unfold tripleProduct
funext x
simp
```

Genuinely independent — it unfolds the *definition* `tripleProduct`, not the theorem
`tripleProduct_const`, so it is real proof work, not the citation exploit. Correctly classified.

**The proof is byte-identical across all three runs, control and treatment alike.**

### Why that identity is the whole result

It rules out both of the usual escape hatches:

- **Not noise.** 3/3 reproducible, same problem, same proof text.
- **Not the pivot machinery.** Gates-off found the identical proof by the identical route. Whatever
  produced this solve, the diagnosis/reformulation path was not it.

So this is not "N=1 per arm, can't conclude." It is a clean null for the pivot gates, and a firmer
one than the raw 1-vs-1 count suggests.

### What did change vs. round 1 (0 → 1 solve)

The improvement appears in **both** arms, so it is attributable to a change present in both — most
plausibly **citation-keeps-searching**, which lets a problem continue past a citation instead of
terminating. `tripleProduct_const` aborted at 19 turns in round 1's control; here it solves.

Stated at the right strength: this is a plausible mechanism on n=1, not an established one.

---

## Corrections to earlier claims

Two claims made during this round do not survive the data and should not propagate.

### 1. "Reformulation compile rate rose 10% → 67%" — arithmetic error; the manipulation check does **not** pass

The Stage 1 report gave "6 reformulations fired, 4 compiled (67%)." The logs show **15
invocations** (one per distinct problem), not 6. Actual per-run figures, counting invocation →
accepted (compiling) sketch:

| Run | Invocations | Accepted | Rate |
|---|---|---|---|
| Round 1 (no error feedback) | 10 | 1 | 10% |
| `Stage1Treat` | 15 | 4 | **27%** |
| `AB2T1` | 16 | 1 | **6%** |
| Both round-2 treatment runs | 31 | 5 | **16%** |

The run-to-run spread (6%–27%) is **wider than the claimed improvement over baseline** (10% → 16%).
`bcd1f4d`'s effect on compile rate is therefore **not established by this data**. The manipulation
check should be recorded as inconclusive, not passed.

Consequence for interpretation: the null on the pivot gates still stands on its own evidence (the
control found the identical proof), but we **cannot** additionally claim "the machinery was given a
fair chance to compile." Compile rate remains low — 5 of 31 invocations.

### 2. "First evidence that the pivot machinery can produce a genuine proof the baseline couldn't" — disproven

Stated in the Stage 1 report on treatment-only data. The control arm subsequently found the same
proof with the gates off. Retracted.

---

## New finding: reformulations are frequently rejected as structurally invalid

Not previously measured. Breakdown of reformulation attempt outcomes:

| Outcome | `Stage1Treat` | `AB2T1` |
|---|---|---|
| Accepted (compiled) | 4 | 1 |
| **Rejected by structural gate** | **9** | **8** |
| Did not compile | 11 | 15 |

When asked to pivot to a different proof strategy, the model frequently returns a sketch that
**modifies the declaration signature** — changing the theorem statement rather than the proof. The
structural gate catches it, but this is the same instinct as the citation exploit wearing a
different hat: under pressure the model alters the problem instead of solving it.

This is a substantive result about *why* the pivot path fails, distinct from "reformulations don't
compile."

---

## Conclusion

1. **Pivot gates: null, well-controlled.** Identical proof in both arms. Second null for this
   machinery; this one is not confounded by the round-1 issues.
2. **Compile-rate fix: unproven.** Real rate is 6%–27% across runs vs. 10% baseline — spread
   exceeds effect.
3. **The citation attractor remains the binding constraint.** 5–8 citations detected per run. Even
   with detection *and* keep-searching, exactly one problem broke through to an independent proof —
   and the pivot machinery had nothing to do with it. Successful reformulations largely landed back
   on another citation or on a modified declaration.

The remaining intervention that changes what the model *can* do, rather than what we count, is
**citation prevention** — removing or renaming the original declaration at corpus-generation time
so `exact <original>` does not resolve. Everything since round 1 has been downstream mitigation of
a hole that is still open upstream. See `docs/CITATION_EXPLOIT_GATE.md` §"Design decision" for the
prevention proposal.

## Why this stopped at 3 of 6 runs

Runs 4–6 were abandoned: the bench process hangs under parallel load (suspected undisposed
`AsyncSession` in the persist path; diagnosis is uncertain — reports variously describe a mid-run
stall and a post-completion hang, which are different bugs). Filed as maintenance, not fixed here.

The stop was a judgement call, not a capitulation: three runs with a byte-identical proof in both
arms already answer the question. Runs 4–6 would have refined a confidence interval around a
direction that is unambiguous. Recorded so the incompleteness is not mistaken for a suppressed
negative.

## Reproduction

- Data: `bench-2026-08-04_11-32-29.json` (Stage1Treat), `bench-2026-08-04_12-35-31.json` (AB2C1),
  `bench-2026-08-04_12-57-40.json` (AB2T1).
- Logs: `stage1-treatment-2026-08-04_11-04-45.log`, `ab2-AB2C1-2026-08-04_12-16-38.log`,
  `ab2-AB2T1-2026-08-04_12-16-38.log`.
- Corpus: `data/swarm_pivot_ab/` (17 problems — the residual the baseline did not independently
  solve). Config: `--parallel 12 --max-episodes 3 --max-turns 10 --graph-first`, `NEXUS_PIVOT_GATES`
  the only variable.
- Citation classification is now inline (`CitationDetector`), validated against the known-correct
  13-independent/4-citation baseline split before use.
