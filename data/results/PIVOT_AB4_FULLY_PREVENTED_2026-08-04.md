# Pivot-gate A/B, round 4 — fully-prevented corpus (all 17 targets deleted)

**Date:** 2026-08-04 · **Corpus:** `data/swarm_pivot_ab_prevented/` after the deletion fix — **17/17
targets deleted**, zero `sorry`-the-target fallbacks · **Both arms completed.**

**Headline: null, for the fourth time. 1 independent solve in each arm — the same problem, the same
proof. This was the last untested objection, and it did not change the answer.**

---

## Why this round existed

Round 3 ran on a corpus where only **7 of 17** targets were genuinely deleted; the other 10 fell
back to `sorry`-ing the target, which left the name resolvable and degraded those problems to a
post-hoc `#print axioms` rejection with no same-turn compile error. That was the one remaining way
to argue the pivot machinery still hadn't had a fair test.

That gap is now closed (all 17 hard-deleted), and the round was re-run. The prediction recorded
before running was: *"My expectation is no — the gates produced nothing on the 7 problems that
already had hard deletion — but that's a prediction, not a result."* The prediction held.

## Results

| | Arm A — gates OFF | Arm B — gates ON |
|---|---|---|
| **Independent solves** | **1** | **1** |
| Which | `tripleProduct_const` | `tripleProduct_const` |
| Problems completed | 17 | 16 (see caveat) |
| Turns | 309 | 310 |
| **Cost** | **$0.431** | **$0.550** (+28%) |
| Reformulations fired / accepted | 0 (n/a) | 15 / **1** |
| Citations of sorry-backed dependents, rejected | 10 | 17 |

The solve is byte-identical to all six prior runs across four rounds:

```lean
unfold tripleProduct
funext x
simp
```

## The citation instinct just relocates

With every target deleted, citing the answer is impossible. The model's response was not to start
doing proof search — it was to cite the **sorry-backed dependents** left behind by the deletion
repair (`ame_3_2_exists` and friends), which `#print axioms` then rejected: **10 such rejections in
control, 17 in treatment**.

That is the third distinct form the same instinct has taken across these rounds:

1. cite the target (round 1–2) → blocked by detection
2. rewrite the declaration signature (round 3, 9 of 15 reformulations) → blocked by the structural gate
3. cite a sorry-backed neighbour (round 4) → blocked by `#print axioms`

Each mitigation redirected the behaviour rather than converting it into proof search.

## Conclusion across four rounds

| Round | Corpus | Control | Treatment |
|---|---|---|---|
| 1 | original | 0 | 0 |
| 2 | + detection + keep-searching | 1 | 1 |
| 3 | prevented (7/17 hard-deleted) | 1 | 1 |
| **4** | **fully prevented (17/17 hard-deleted)** | **1** | **1** |

The pivot gates have never produced an additional independent solve, under every condition that was
argued to be unfair to them. **The recommendation to stop investing in them stands, and is now
about as well-supported as an N=17 experiment can make it.**

Reformulation compile rate remains the proximate mechanism: 1 of 15 (7%) this round, consistent
with 10% / 6–27% / 7% before it.

## Caveats

- **Treatment completed 16 of 17 problems.** `not_isThick_of_finite` died on a transient
  `HttpRequestException: Connection reset by peer` from DeepSeek — a network fault, not a code
  defect. That problem aborted in control this round and in every prior round, so it is not a
  plausible lost solve, but the arms are 17 vs 16 and that is stated rather than smoothed over.
- **`unknown identifier` counts are not measurable from the bench log.** The log carries only
  LeanOracle's own summary lines, not Lean's error text, so this round cannot quantify how often
  the model *attempted* a now-impossible target citation — only that it succeeded zero times. The
  visible signal is the sorry-backed-dependent rejections above.
- N=17, one run per arm; the direction is consistent across four rounds but no single round is
  statistically powered.
- Control ran first and both arms share one Neo4j, so fossil carryover is directional.

## Reproduction

```bash
python3 scripts/generate_prevented_corpus.py \
    --stub-dir data/swarm_pivot_ab --out-dir data/swarm_pivot_ab_prevented --clean
# verify 17/17 report citation=deleted before trusting a run

NEXUS_PIVOT_GATES=0 ... --source PREV2C1 --parallel 12 --max-episodes 3 --max-turns 10 --graph-first
NEXUS_PIVOT_GATES=1 ... --source PREV2T1 --parallel 12 --max-episodes 3 --max-turns 10 --graph-first
```

Data: `bench-2026-08-04_17-02-36.json` (control), `bench-2026-08-04_17-28-44.json` (treatment).
Raw logs are not committed (see the note in `PIVOT_AB2_NULL_RESULT_2026-08-04.md`).

Fresh source tags (`PREV2C1`/`PREV2T1`) again: the round-3 tags had 2 problems marked `Solved` in
Neo4j, which `Program.cs`'s already-solved check would have silently skipped.
