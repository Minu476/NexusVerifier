# Pivot gates postmortem — built, tested four ways, null, removed

**Lived:** 2026-08-03 → 2026-08-14 (commits `37b0f5f`..`f603df7`, removed on
`fix/remove-pivot-gates`). This document exists so the machinery is not rebuilt.

## What was built

Two gates inspired by the OAI reasoning walkthroughs' diagnose-then-pivot pattern:

1. **Obstruction-diagnosis gate** (in-episode): on the 2nd consecutive structural
   violation, instead of aborting, force a natural-language LLM call asking the model
   to name the obstruction and propose a different approach; inject the diagnosis into
   the next prover prompt.
2. **Between-episode reformulation gate** (orchestrator): after N stuck episodes,
   force a full proof-strategy pivot — a new sketch from a diagnosis prompt, with one
   compile-error-feedback retry.

Both were behind `NEXUS_PIVOT_GATES` / `PivotGatesEnabled` for A/B.

## The four rounds

| Round | Corpus | Control | Treatment | Treatment cost |
|---|---|---|---|---|
| 1 | original (17) | 0 independent | 0 independent | — |
| 2 | + citation detection + keep-searching | 1 | 1 | — |
| 3 | prevented (7/17 hard-deleted) | 1 | 1 | — |
| 4 | **fully prevented (17/17 hard-deleted)** | **1** | **1** | +28% |

Every round null: the single solve (`tripleProduct_const`) was byte-identical across
all seven runs. Treatment reformulations compiled 1 in 15 (~7%). Full writeups:
`data/results/PIVOT_AB_2026-08-03.md`, `PIVOT_AB2_NULL_RESULT_2026-08-04.md`,
`PIVOT_AB3_PREVENTED_2026-08-04.md`, `PIVOT_AB4_FULLY_PREVENTED_2026-08-04.md`.

## What was actually learned (the part worth keeping)

The citation instinct **relocates** under each mitigation instead of converting into
proof search: cite the target → blocked by `CitationDetector`; rewrite the signature
→ blocked by the structural gate; cite a `sorry`-backed dependent → blocked by
`#print axioms`. That is a property of the prover under evaluation pressure. **The
lever is not "block one more shortcut."** If you build the next experiment, assume a
fourth relocation form exists and design the measurement to catch it before trusting
the count.

## What was kept vs removed (2026-08-14)

**Kept** — measurement integrity, not pivot machinery:
- `CitationDetector` + its 11 tests (unconditional solve-time gate; round 1's "solves"
  were 100% citations — without it every number in this repo is fiction)
- The structural-validity gate, tier demotion on 1st violation, abort on 2nd
  (`EpisodeOutcome.StructuralGateRejection`), and the orchestrator's
  defense-in-depth re-check
- The citation-keeps-searching behavior (a detected citation marks the outcome but
  the search continues)

**Removed** — the pivot experiment itself: both gates, the flag, the prompts
(`BuildDiagnosisRequest`, `BuildReformulationRequest`), the diagnosis plumbing, and
their tests. Termination-on-hacking coverage retained in
`NexusProverSubagentTests.RunEpisodeAsync_AlwaysStructuralViolation_AbortsEarlyWellBeforeMaxTurns`.

## Follow-up: the fails-open audit question

For each gate in the pipeline: **what does it do when it doesn't know?** If the
answer isn't "fail visibly," it's a candidate defect. Known instance fixed alongside
this removal: the bench already-solved check silently *skipped* problems whose id was
tagged `Solved` in Neo4j — nearly invalidated round 3 — now logs a WARN naming the
problem and the stale-tag hypothesis. Remaining candidates to audit on the next pass:
fossil-seeding failures (does an empty store fail loudly?), encoder fallbacks, and
router circuit-breaker fallbacks.
