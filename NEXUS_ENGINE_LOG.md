# NexusAgent — ErdosHypergraph Engine Development Log

Engineering journal for `formal-conjectures/_nexus_tmp/ErdosHypergraph.lean`.
Each entry is anchored to a real git commit. Honest numbers only.

---

## Baseline (2026-05-28 08:50 · commit `394d161`)

**What existed**: A Lean-native AND/OR backward-chaining hypergraph engine
in a single `#eval` block. Seeds were harvested from 15 Mathlib seed groups
(`seed_nat_comm`, `seed_le_chain`, etc.). The search used string matching on
pretty-printed goal types (`ppExpr`). No FC100 goals injected yet.

**Key design**: `buildHypergraph` → `HypergraphG.backwardEdges` →
`searchProof` (string-based, no unification).

---

## Phase 1: FC100 injection + domain seeds (2026-05-28 09:06–09:35)

### `fcb321d` — FC100 node injection + full AND/OR search
Added `§8`: the 100 FC100 declarations from `FC100SolvedSet1` are injected as
goal nodes. The engine runs AND/OR search over all 100. Initial result: **16/100
PROVED** using only Mathlib string-shape matches.

### `b0b3495` — 16 domain-seed wrappers → 16/100 + JSON
Added `§9`: `def seed_X := X` pattern for 16 closed FC100 declarations.
`getUsedConstants` harvests the proof term; `extractEdge` finds leaf edges
whose output strings match injected goal strings. Added `g.toJSON` serialization.

**Design insight documented in §9 KEY INSIGHT comment**:
> `seed_X := X` → `getUsedConstants = [X]` → leaf edge with output = `ppExpr(X.type)` → matches the injected goal string → PROVED.

This is **by design circular** for goals whose statement is the seed: works correctly as a lookup of known proved theorems.

---

## Phase 2: isDefEq unification (2026-05-28 11:13–11:33)

### `220ff7c` — isDefEq replaces string matching
Replaced `ppExpr` string comparison with `Lean.Meta.isDefEq` unification via
`MetaM`. `searchProofMeta` introduced: takes an `Expr` goal type, opens a fresh
`MetaM` context, uses `forallMetaTelescope` on each seed to introduce mvars,
then `isDefEq goal concl` to attempt matching. Mvar-bound premise types
collected as sub-goals.

**Key soundness mechanism**: `withoutModifyingState` wraps probe attempts to
prevent side effects on mvar state.

### `0bb6718` — depth-2 isDefEq search → 100/100 FC100 PROVED
`tryCloseD2` added: for each edge, tries to close all Prop-kinded premises at
depth-1. With `fc100Decls` itself added to the seed harvest
(`allSeeds = seedNames ++ domainSeedNames ++ fc100Decls`), **100/100 FC100
goals proved**. This is the first UNSOUND result — all 100 prove because each
goal's own proof term is in the seed harvest.

**Negative controls** added: `1 = 2`, `0 = 1`, `False`, `Nat.Prime 4`.
At this point they PASSED (all GAP), indicating soundness for false goals,
but the 100/100 is still inflated by self-harvest.

---

## Phase 3: Soundness fix (2026-05-28 12:03–12:52)

### `c56b731` — CRITICAL SOUNDNESS FIX: tryCloseD1Commit
**Bug**: `tryCloseD1Commit` (the committing variant used in D2 chains) was using
`withoutModifyingState` — which discards all mvar assignments, including the ones
from closing premise N that constrain premise N+1. This made premise-chained
proofs unsound.

**Fix**: `saveState`/`restoreState`-on-failure pattern. Success returns BEFORE
`restoreState`; failure calls `restoreState saved`. Assignments commit for
successful sub-goals.

**Result**: With all 100 FC100 in the harvest, some goals that appeared PROVED
were actually proved by the mvar leak. After fix: **reduced to honest results**.

### `03d2eed` — docs: explain withoutModifyingState failure mode
Added permanent comment block in §2.6 explaining why `withoutModifyingState`
is wrong for `tryCloseD1Commit` and correct for `tryCloseD1` (read-only probe).

### `bc9182d` — 4 new domain seeds → 20/100 PROVED
After soundness fix, dropped from 100/100 to 20/100 (honest). Added 4 new
proved FC100 declarations as domain seeds:
- `Erdos42.example_maximal_sidon` : `{1,2,4}.IsMaximalSidonSetIn 4`
- `Erdos141.first_three_odd_primes` : `{3,5,7}.IsPrimeProgressionOfLength 3`
- `CongruentNumber.congruentNumber_7` : `congruentNumber 7`
- `Erdos678.lcmInterval_lt_example3` : `lcmInterval 62 8 < lcmInterval 52 7`

Negative controls: all 4 → GAP (correct). **Sound.**

---

## Phase 4: Binder stripping + assumption fallback + gap-map (2026-05-28 13:50)

### `cfd3670` — forallTelescope + assumption check + gap-map → 32/100

Three architectural improvements landed together:

**1. `forallTelescope` binder stripping in `searchProofMeta`**
Goals with leading `∀` binders (e.g. `∀ {d : ℕ} (hd : 2 ≤ d), ExistsAME 2 d`)
previously couldn't match seeds because `isDefEq (∀ binders...) (conclusion)`
fails structurally. Fix: strip goal binders via `forallTelescope`, producing
`body = ExistsAME 2 d_fv` with fvars in the local context. Then match body
against seed conclusions.

**Soundness key**: `forallTelescope` introduces **fvars** (opaque constants),
not mvars. `MetaM.run` initializes a fresh local context — no ambient hypotheses
can leak in. `getLCtx` inside the callback sees only the goal's own ∀-bound
hypotheses.

**2. Assumption fallback in `tryCloseD1Commit`**
For Prop-kinded ∀ binders like `(hd : 2 ≤ d)`, the stripped fvar `hd_fv : 2 ≤ d_fv`
lives in the local context. When a seed premise `2 ≤ d_fv` can't be closed by
any edge, the fallback checks `getLCtx` for a matching Prop local declaration.
Guard: only `Prop`-sorted locals are considered (`declSort == propSort`).

**Bug found during implementation**: `let matches ← ...` fails because `matches`
is Lean 4 term-level syntax (used in `expr matches pat`). Renamed to `hitAsm`.

**3. `GapInfo` structure — gap-map at zero extra cost**
`tryCloseD2` now returns `(Option steps, Option GapInfo)`. When a proof fails,
`GapInfo` records which edge came closest (matched the goal body but couldn't
close all premises), which premises WERE closed, and which was the first failure.
Displayed in §8 output when a goal GAPs.

**11 new domain seeds added** (all with `@` for implicit binders):
- Data-only: `count_false_morphism`, `μ_half_eq_uniform`, `boundaryCount_univ`,
  `star_smul_mul_smul`, `firstCol_normSq`, `hasConstantOverlapSq_singleton`,
  `hasGap_empty`
- Prop-binder: `ame_2_exists`, `ame_3_exists`, `KTExtendsK`,
  `maxWeaklyDivisible_one`

**Bug found**: Seeds with `{n : ℕ}` implicit binders fail `def seed_X := X`
elaboration (Lean tries to synthesize `n` and fails). Fix: `def seed_X := @X`
disables implicit insertion; `@` is invisible to `isDefEq` since it only affects
def-site elaboration, not the stored proof term or its type.

**2 new negative controls added**:
- `∀ n, n = n.succ` — parameterized false, exercises binder-strip path → GAP ✓
- `¬ congruentNumber 7` — perturbation of a PROVED goal → GAP ✓

**Results**: **32/100 PROVED**, all 6 negative controls GAP. Bonus: `tripleProduct_const`
proved by 3-step composition `le_refl × 2 + LE.le.antisymm`.

---

## Phase 5: Leave-one-out measurement (2026-05-28 16:30)

### `33a4503` — §10 leave-one-out control → 4/100 genuine

**Motivation**: Opus 4.8 review correctly identified that `allSeeds = seedNames
++ domainSeedNames ++ fc100Decls` uses each FC100 goal's own proof term as a
seed, making most of the 32 PROVED results circular self-lookups.

**§10 experiment**: Build the seed graph with ONLY `seedNames` (15 generic
Mathlib seeds, no domain wrappers, no fc100Decls harvest). Re-run all 100 FC100
goals. Survivors require no knowledge of their own proofs.

**Result**: **4/100 genuinely composed**:
| Goal | Proof | Via |
|------|-------|-----|
| `OeisA67720.a_6` | 1-step | `Nat.add_comm` |
| `PellNumbers.pellNumber_two` | 1-step | `Nat.mul_comm` |
| `WrittenOnTheWallII.Test.C6_size` | 1-step | `Nat.add_comm` |
| `OeisA67720.a_1` | 1-step | `Nat.add_comm` |

**28 of 32 are self-harvest** (circular). `tripleProduct_const`, despite its
3-step composition trace, also falls in this category — its lemmas
`le_refl`/`LE.le.antisymm` reach the right types only because the target's
own proof populated the edge graph.

**Conclusion**: The honest reach on genuinely unsolved problems is **~4**, not 32.
The 32 number is correct as a "known theorems the engine can identify", but it
does not predict performance on open conjectures.

---

## Current State (2026-05-28)

| Metric | Value |
|--------|-------|
| Engine | `formal-conjectures/_nexus_tmp/ErdosHypergraph.lean` |
| Lean version | 4.27.0 ARM64 macOS |
| §8 PROVED (full seeds) | **32 / 100** |
| §10 GENUINE (generic seeds only) | **4 / 100** |
| Negative controls | **6 / 6 GAP** (sound) |
| Search depth | ≤ 2 (D1 for premises, D2 for goal) |
| Seed count | 15 generic + 31 domain wrappers |
| Open conjectures proved | **0** |

---

## Known Limitations and Next Steps

**Why the engine can't prove open conjectures yet:**
- All generic seeds are Mathlib API lemmas (arithmetic, order theory, combinatorics).
  Open conjectures require domain-specific mathematical steps not in any generic seed.
- The assumption fallback only closes premises that are literally ∀-bound hypotheses
  of the goal — it cannot construct new proofs.
- Search depth ≤ 2 limits chains to: apply one edge + close its premises.

**Honest path to progress on open conjectures:**
1. **Expand generic seed coverage** — add seeds from relevant Mathlib modules
   (number theory, graph theory, combinatorics) that form reusable building blocks.
2. **Depth-3 search** — allow one more level of premise decomposition. Currently
   deferred because the search space grows quadratically.
3. **Tactic synthesis** — instead of (or in addition to) backward chaining,
   generate tactic proofs (`norm_num`, `decide`, `omega`) for closed numerical claims.
4. **Conjecture-specific seeds** — for a target open problem, manually seed its
   known related lemmas. This moves the honest reach without self-harvest inflation.

**Architecture notes:**
- `forallMetaTelescope` on the GOAL side is UNSAFE (would reintroduce the old
  `withoutModifyingState` vulnerability by creating underconstrained mvars).
  Always use `forallTelescope` (fvars) on the goal side.
- The `§10` LOO block should be re-run whenever new seeds are added to verify
  that the genuine count is actually increasing, not just the circular count.
