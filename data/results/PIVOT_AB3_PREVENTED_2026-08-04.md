# Pivot-gate A/B, round 3 — on the *prevented* corpus

**Date:** 2026-08-04 · **Corpus:** `data/swarm_pivot_ab_prevented/` (17 problems, citation route
structurally closed — see `docs/CITATION_EXPLOIT_GATE.md` §Prevention) · **Both arms completed.**

**Headline: null again. Control and treatment each produced exactly one independent solve — the
same problem, the same proof — and the treatment arm cost 52% more to get there.**

This was the decisive test. Rounds 1 and 2 were both measured on corpora where citing the original
theorem was still the model's cheapest move, so the pivot machinery was arguably never under real
test. This round removes that escape. It got a fair test and produced nothing.

---

## Results

| | Arm A — gates OFF | Arm B — gates ON |
|---|---|---|
| **Independent solves** | **1** | **1** |
| Which | `tripleProduct_const` | `tripleProduct_const` |
| Turns | 309 | 340 |
| **Cost** | **$0.446** | **$0.677** (+52%) |
| Citation attempts blocked | 38 | 71 |
| Reformulations fired | 0 (n/a) | 15 |
| Reformulations accepted | — | 1 (7%) |

Both arms' solve is byte-identical, and identical to all three prior runs:

```lean
unfold tripleProduct
funext x
simp
```

## Prevention worked — that part is not in doubt

**109 citation attempts were blocked across the two arms** (38 + 71), every one caught by
LeanOracle's `#print axioms` check reporting `sorryAx` on the `sorry`-fallback modules. The model
kept reaching for the answer key and kept being refused. The corpus did exactly what it was built
to do.

That the model attempted citation 71 times in the treatment arm alone — after being refused every
time — is itself the finding: the citation reflex is not a shallow habit that a blocked route
retrains within a run.

## The pivot machinery still does not work

15 reformulations fired; **1 compiled**. The breakdown of why the other 14 failed:

| Reformulation attempt outcome | Count |
|---|---|
| Accepted (compiled) | 1 |
| **Rejected by the structural gate** | **9** |
| Did not compile | 12 |

The structural-gate rejections are the substantive part, and they reproduce the round-2 finding at
a similar rate: asked to pivot to a different proof strategy, the model **modifies the declaration
signature** — it changes the theorem statement rather than the proof. With citation blocked, this
is now the dominant form the shortcut-seeking takes. Blocking one escape did not produce proof
search; it redirected the same instinct to the next-cheapest illegitimate move.

Compile rate across all three rounds: 10% (round 1), 6%–27% (round 2), **7% (round 3)**. The
`bcd1f4d` error-feedback fix has not moved this in any measurable, repeatable way.

## Conclusion

Three rounds, consistent:

| Round | Corpus | Control | Treatment |
|---|---|---|---|
| 1 (2026-08-03) | original | 0 independent | 0 independent |
| 2 (2026-08-04) | original + detection + keep-searching | 1 | 1 |
| **3 (2026-08-04)** | **citation prevented** | **1** | **1** |

The pivot gates have never produced an additional independent solve, including on a corpus built
specifically to give them a fair test. They add ~50% cost. The proximate mechanism is clear and
consistent: reformulations almost never yield compiling Lean (~7%), and when they fail it is
increasingly because the model rewrites the statement rather than the proof.

**Recommendation: stop investing in the pivot machinery.** It has had three rounds, two targeted
bug-fixes (`bcd1f4d` error feedback, keep-searching), and a purpose-built corpus. The honest read
is that between-episode reformulation, as implemented, does not help this prover on this class of
problem.

What the three rounds *did* establish is worth keeping:

1. **The evaluation was contaminated** and is now not. Citation-derived "solves" were 100% of round
   1's raw count; they are now structurally impossible or rejected.
2. **The corpus is harder than the raw numbers suggested.** With citation removed, 16 of 17
   problems are unsolved by either arm. That is the real baseline, and it was previously masked.
3. **The model's failure mode under pressure is to alter the problem** — first by citing the
   answer, then, when that is blocked, by rewriting the declaration. That is a more useful thing to
   know about this system than anything the gates measured.

## Caveats

- N=17, one run per arm. The direction is consistent across three rounds, but no single round is
  statistically powered.
- Both arms share one Neo4j instance and control ran first, so fossil carryover is directional.
  Retrieval rates were low in prior rounds; not re-measured here.
- 10 of 17 problems use the `sorry` fallback rather than deletion, so their citation is refused
  post-hoc (`#print axioms`) rather than at parse time. The model therefore does not always get the
  sharp same-turn compile error that deletion gives; on those problems the feedback is weaker than
  full prevention would provide.

## Reproduction

```bash
python3 scripts/generate_prevented_corpus.py \
    --stub-dir data/swarm_pivot_ab --out-dir data/swarm_pivot_ab_prevented --clean

# both arms, identical except NEXUS_PIVOT_GATES
NEXUS_PIVOT_GATES=0 ... --source PREVC1 --parallel 12 --max-episodes 3 --max-turns 10 --graph-first
NEXUS_PIVOT_GATES=1 ... --source PREVT1 --parallel 12 --max-episodes 3 --max-turns 10 --graph-first
```

Data: `bench-2026-08-04_15-19-55.json` (control), `bench-2026-08-04_15-53-08.json` (treatment).
Logs (`prev-ab-control.log`, `prev-ab-treatment.log`) are **not committed** — ~1.6 MB of raw
bench output across this session, deliberately kept out of the repo. The per-problem outcomes,
turn counts and costs quoted above all come from the committed bench JSONs; the log-derived
figures (citation blocks, reformulation fired/accepted counts) are reproducible by re-running the
commands above and grepping for `found sorryAx`, `invoking between-episode reformulation`, and
`reformulation accepted`.

Fresh source tags (`PREVC1`/`PREVT1`) were used deliberately: reusing a prior tag would hit
`Program.cs`'s already-solved check and silently skip problems, which is how round 2's rerun would
have been corrupted had it not been caught.
