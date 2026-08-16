# Transition-dump A/B — result: null, with net harm signal (b=0, c=2)

**Run:** 2026-08-15 (overnight). **Design (pre-registered, incl. MDE and
interpretation ladder):** `TRANSITION_DUMP_AB_DESIGN_2026-08-14.md`.

## Headline

| | Control (`TDABC1`) | Treatment (`TDABT1`) |
|---|---|---|
| Store seeding | 54 historical fossils | 54 + **4,863 dump fossils** (1,797 goals, 4,922 edges) |
| Neo4j | 27687 (shared) | 27690 — cold volume copy of post-control state |
| Independent solves | **13/31** | **11/31** (28 completed, see §3) |
| Spent | $0.89 | $0.91 |
| Store proposals fired | 26 | 29 (best sim 0.968, succ 1.00) |

**Paired (28 common problems): b (treatment gains) = 0, c (treatment losses) = 2,
exact McNemar p = 0.50.** Losses: `congruentNumber_7` and `tripleProduct_const` —
the latter being the one problem every prior run since 2026-08-03 had solved.

Pre-registered interpretation (design §8, committed before the run): **"c > b
(net harm): dump seeding actively hurts at this scale — gate thresholds
recalibrated or the store retired."** That is the branch we are on. It is not
statistically significant (p=0.5 needs c≥5-ish at b=0), and the pre-registration
already declared n=31 directional-only; but the DIRECTION is consistent with the
store's entire history (−3 ungated, −1 gated, now −2 with a 4,922-edge store).

## What the run established positively

1. **The whole pipeline works end-to-end**: InfoTreeWalker as a Mathlib-style
   module-file linter (NexusDump sub-package), 241,777 transition rows from
   149/151 corpus files, 4,863 tactic-shaped fossils ingested, store seeded,
   proposals firing at 0.968 similarity. The "near-empty store" excuse is now
   dead: a real, verified transition store existed and was consulted 29 times.
2. **Control is a clean baseline reproduction**: 13/31, byte-for-byte the 2026-08-03
   baseline number, on pivot-gates-removed code — the Phase-2 removal did not
   shift the baseline.
3. **Isolation held**: verification query — control db MATHLIB_DUMP count **0**,
   treatment db **4,863**. No cross-contamination (both arms also threw the
   identical pre-existing `goalshape_state_vec_idx`-missing errors — 62 vs 31 —
   that legacy path has been broken since before the 08-03 baseline, so arm
   parity on it is exact).

## The sharpest single observation

The dump **contains `tripleProduct_const`'s own proof** (it lives in
`Arxiv/1609.08688/sIncreasingrTuples.lean`). The treatment store therefore held
the answer to the one problem it lost that control solved — the closest goal
match in the whole run — and the run still aborted it ("2 consecutive episodes
with no improvement"). Retrieve-the-right-tactic is not the bottleneck;
**surfacing a retrieved tactic as a usable, compiling next step is.** The store
proposes, nothing in the loop converts proposals into accepted proof steps at a
rate that matters.

## Deviations from the design (logged, per pre-registration)

1. **Treatment completed 28/31.** Three problems (`isClusterPrime_97_isLeast_non_cluster`,
   `maxWeaklyDivisible_one`, `not_isThick_of_finite`) crashed with DeepSeek
   **HTTP 402 Payment Required** — the account ran out of credit mid-run. All
   three were control-unsolved (outcomes Aborted / EpisodeBudgetExhausted), so
   the paired comparison loses only low-probability chances for treatment; had
   all three solved, treatment would be 14/31 — still not a positive result
   (b would be 3, c 2, p≈1.0). DeepSeek needs a credit top-up before any future
   bench run.
2. Dump coverage 149/151 files (Encoding, BusyBeavers fail under the injected
   walker import). Immaterial to the result.

## Disposition

- The transition-dump pipeline stays as an asset (it is real, cheap to re-run,
  and the corpus is now dumpable at will). The **store-as-seeded-retriever
  hypothesis is retired** on this evidence — three data points in the same
   direction, now with the emptiness excuse removed.
- Next lever per the pivot-gates postmortem and the OAI-walkthrough lesson: the
  conversion layer (retrieved tactic → elaborated step), not more retrieval.
- Artifacts: `bench-2026-08-15_01-38-45.json` (control),
  `bench-2026-08-15_02-18-26.json` (treatment) + meta; raw logs untracked
  (`tdab-*.log`); runner `scripts/run_tdab.sh`; analyzer `scripts/analyze_tdab.py`;
  dump mechanism in the formal-conjectures fork (NexusDump + `scripts/dump_transitions.sh`).

## Reproduction

```bash
./scripts/run_tdab.sh control    # arm 1 vs 27687
./scripts/run_tdab.sh isolate    # cold copy → 27690, seed-dump, verification query
./scripts/run_tdab.sh treatment  # arm 2 vs 27690
python3 scripts/analyze_tdab.py data/results/bench-2026-08-15_01-38-45.json data/results/bench-2026-08-15_02-18-26.json
```
