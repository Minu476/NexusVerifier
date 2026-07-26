# FC100 "gap-set" experiments: methodology and a corpus-selection bug

This note documents the ad-hoc "gap-set" / swarm experiments run against
`nexus bench` this cycle (problem IDs prefixed `Erdos_gap*`,
`ErdosV2Fixed_gap*`, `SwarmV1_gap*` in Neo4j `nexusdb`), and a root-cause
finding from auditing their results: **the experiments drew their "unsolved"
problems from the wrong upstream corpus.** This is separate from, and should
not be confused with, the sorry-citation-laundering bug fixed earlier this
cycle (see below for how they differ).

## What these experiments were

The README's existing "Benchmark results — FC100" section describes a
narrower, already-correct use of `FC100SolvedSet1`: mechanically verifying
*pre-existing* Lean proofs from that corpus under a restricted axiom policy,
with an explicit disclaimer that "this repository does not claim novel
proofs of open Erdős problems."

The gap-set experiments are a different, informal line of investigation that
grew out of that work but was never reconciled with it or written down
permanently: take problems from `FC100SolvedSet1`, strip their proof body,
reconstruct the goal statement as a fresh `theorem ... := by sorry` stub
under a new name (`g_<Name>_new`), and feed the stub to `NexusProverSubagent`
as if it were a genuinely open problem — to see whether more independent LLM
sampling ("swarm": N tries per problem, solved if any succeeds) helps solve
rate. A `LeanOracle`-preflight pass kept only stubs that compiled cleanly as
fresh sorries (currently 100 candidates gathered this way; 54 had not
solved on a single `nexus bench` try before the swarm experiment below).

## Root cause: `FC100SolvedSet1` is not the open-problems corpus

`FormalConjectures/Subsets/README.md` (upstream, unmodified) states plainly:

> -   **FC100OpenSet1** — 100 randomly sampled open research problems.
> -   **FC100SolvedSet1** — 100 randomly sampled problems across the research
>     solved, textbook, API, and test categories.

`FC100SolvedSet1.lean` is a curated list of **100 declarations the upstream
authors already consider solved** — each tagged `@[category research
solved, ...]` (or textbook/API/test) at its original declaration site, with
a complete, `sorry`-free proof. `FC100OpenSet1.lean`, sitting right next to
it, is the corpus that should have been used for anything calling itself a
"gap" or "unsolved problem" set. It was not — the gap-set candidates were
built exclusively from `FC100SolvedSet1`.

This alone would just mean the label "unsolved gap" was wrong; the more
serious consequence is structural: **the reconstructed stub still imports
`FormalConjectures.Subsets.FC100SolvedSet1` (or the original file directly),
so the original, complete declaration remains compiled and citable by name
in the same Lean environment the stub runs in.** "Solving" the stub can
therefore be, and in practice mostly was, a one-line citation of the answer
sitting a few lines away — not proof search.

This is distinct from the sorry-citation-laundering bug fixed earlier this
cycle. That bug was about the pipeline citing *another goal inside the same
benchmark* that was itself still `sorry`'d (caught by adding a `#print
axioms` check — `HasSorryAxiomAsync` in `LeanOracle.cs`). Here, the cited
declaration is **not** `sorry`'d — it's a real, complete, axiom-clean proof.
`#print axioms` correctly reports it clean (verified below), so this is not
a soundness bug in the prover pipeline. It's a **corpus-selection error**:
the benchmark's answer key is in scope by construction.

## Evidence: the July 2026 N=8 swarm run

Run: `nexus bench --source SwarmV1 --parallel 12 --max-episodes 2
--max-turns 8` over the 54 gap-set problems that had not solved on a prior
single-try `nexus bench` pass, 8 independent tries each (432 attempts
requested, 429 completed — 3 short, not investigated).

**Headline numbers:**

| Metric | Value |
|---|---|
| Tries attempted | 429 / 432 |
| Individual tries solved | 63 |
| Base problems (of 54) with ≥1 solved try | 15 (28%) |
| Total cost | $18.47 |
| Wall-clock duration | ~3h 39m |

On its own, 15/54 base problems newly solved (vs. 0/54 on the prior
single-try pass) reads like a positive swarm-sampling signal. It mostly
isn't — see the classification below.

**Classification of all 63 individual solves**, by whether the winning
fossilized tactic block directly names the original `FC100SolvedSet1`
declaration (confirmed via the `ProofFossil.tacticBlock` recorded in
`nexusdb`, cross-referenced against each declaration's actual source):

| Class | Count | Description |
|---|---|---|
| **Citation** | 47 (75%) | Tactic is `exact <original.decl.name> ...` (or a trivial `intro`/`simpa` wrapper around it) |
| **Independent** | 15 (24%) | Tactic doesn't name the original declaration (`rfl`, `norm_num`, `simp [...]`, `unfold ...`) |
| **Unknown** | 1 (2%) | No fossil recorded for the winning try (`gap05__try8`) |

By base problem (15 solved):

- **Citation-only** (every solved try cited the answer) — 8 bases: `gap02`,
  `gap28`, `gap36`, `gap58`, `gap65`, `gap67`, `gap71`, `gap83`
- **Mixed** (some tries cited, some didn't) — 4 bases: `gap31`, `gap50`,
  `gap75`, `gap90`
- **Independent-only** — 2 bases: `gap59`, `gap73`
- **Unknown** — 1 base: `gap05`

Caveats on the "independent" bucket, so it isn't over-read as evidence of
real proof search:

- `gap31__try4`'s fossil text is truncated (just the `import` line) — likely
  also a citation whose fossil got mis-captured. Not corrected in the count
  above, so 15 "independent" is an upper bound (14 is more likely accurate).
- Several genuinely non-citing proofs are still trivial for reasons
  unrelated to search ability: `gap73`'s 7 independent tries are all
  `intro α a; rfl` or `unfold ...; rfl` — the goal is true by definitional
  unfolding, not by an argument the LLM constructed. `gap75` similarly
  leans on `unfold`/`norm_num` directly against the definition.
- The closest things to genuine derivation: `gap50`'s
  `refine ⟨3, 5, ?_, ?_, ?_, ?_⟩ · norm_num ...` (concrete witness search)
  and `gap59`'s `simp [smul_smul, mul_smul_comm, smul_mul_assoc, mul_comm]`
  (an actual rewrite-based simplification).

**Soundness check on a representative citation** (`gap02`,
`OpenQuantumProblem35.ame_3_exists`):

```
theorem g_OpenQuantumProblem35_ame_3_exists_new :
    ∀ {d : ℕ}, 2 ≤ d → OpenQuantumProblem35.ExistsAME 3 d := by
  exact OpenQuantumProblem35.ame_3_exists

#print axioms g_OpenQuantumProblem35_ame_3_exists_new
-- 'g_OpenQuantumProblem35_ame_3_exists_new' depends on axioms:
--   [propext, Classical.choice, Quot.sound]
```

No `sorryAx`. The proof is sound by our own axiom-closure bar — it is simply
not evidence of novel problem-solving.

## Bottom line and recommendation

At most ~22% of the swarm run's 63 individual solves (and likely fewer, per
the caveats above) show anything beyond citing the pre-existing answer or
unfolding a definition to `rfl`. The apparent 28% base-problem solve rate is
overwhelmingly explained by answer-key visibility from an incorrect corpus
choice, not by swarm sampling helping the prover search harder.

Any future "unsolved gap" experiment must:

1. Draw candidate problems from `FC100OpenSet1.lean`, not
   `FC100SolvedSet1.lean` — that's what it's for.
2. Even then, verify the reconstructed stub's import closure doesn't expose
   a differently-named but logically-identical already-proved lemma
   (`FC100OpenSet1` problems are open precisely because no such lemma
   should exist upstream, but this should be checked, not assumed).
3. Keep this distinct from the `HasSorryAxiomAsync` check
   (`LeanOracle.cs`) — that check guards against citing something *still
   unsorried within the benchmark*; it cannot and should not be expected to
   catch citation of a genuinely complete, correctly-provenanced upstream
   proof. The fix for *this* class of leak is corpus selection at
   construction time, not a compile-time check.

## Follow-up: rerunning against the correct corpus (`FC100OpenSet1`)

Recommendation 1 above was carried out the same cycle. Building the corrected
gap-set surfaced one more thing worth recording before the results: **most of
`FC100OpenSet1` cannot be used for this methodology at all, by construction.**

### `FC100OpenSet1` has two structurally different kinds of "open"

Of `FC100OpenSet1.lean`'s 100 `decl_name%` entries, 94 resolved to a source
declaration (6 didn't — dotted-name/grep mismatches, not investigated). Of
those 94, **60 use formal-conjectures' `answer(sorry)` idiom** — e.g.
Büchi's problem:

```
theorem buchi_problem :
    answer(sorry) ↔ ∃ M : ℕ, 1 ≤ M ∧ IsBuchi M := by
  sorry
```

`answer(sorry)` elaborates to a `sorry`-produced placeholder *inside the
statement itself* — the correct answer (true/false, or a specific bound) is
unknown, not just the proof. Any proof of a statement shaped like this
necessarily has `sorryAx` in its axiom closure **no matter how it's proved**,
because the statement required `sorry` to even typecheck. These are
structurally inadmissible for a "produce a `sorryAx`-free proof" pipeline —
not a pipeline limitation, a category mismatch. Only the remaining **34
"plain" problems** (fully-specified statement, e.g. `buchi_problem_M5 :
IsBuchi 5 := by sorry`) are valid candidates.

### Corrected construction

For each of the 34 plain candidates: extracted the fully-qualified type via
`#check @<decl>` against a fresh Lean process (not hand-reconstructed —
the same class of mistake, guessing a pretty-printed type instead of asking
Lean for it, has already cost real bugs this cycle), built a fresh
`theorem g_<Name>_new : <type> := by sorry` stub importing
`FormalConjectures.Subsets.FC100OpenSet1`, and preflight-compiled each.
**30/34 type-checked; 23/34 preflight-compiled clean** as fresh sorry stubs.
The 23 survivors are genuinely famous open problems: the smooth 4-dimensional
Poincaré conjecture, Mandelbrot set local connectivity (MLC), the (p-adic)
Littlewood conjecture, Gilbreath's conjecture, Selfridge's conjecture,
Grimm's conjecture, the class number problem, transcendence of
$\pi^{\pi^{\pi}}$, and others.

### Result: `nexus bench --source OpenSet1V1 --parallel 8 --max-episodes 2 --max-turns 8`

| Metric | Value |
|---|---|
| Problems attempted | 23 |
| **Solved** | **0** |
| Aborted | 21 |
| Budget exhausted | 2 |
| Timed out | 0 |
| Total cost | $1.07 |
| Duration | ~23 min |

Confirmed in `nexusdb` independently of the log: every `MathProblem` node
under `OpenSet1V1` has `status = "InProgress"` — none reached `Solved`. With
zero solves there's nothing to spot-check for citation-laundering, which is
itself the point: **0% is the correct, expected result here**, not a
regression or a pipeline failure.

Two secondary signals worth recording, since they show the existing defenses
held up under real pressure rather than simply never being exercised:

- **`Struct rejects: 13`** (42 individual rejection events across turns) —
  the LLM did attempt reward-hacking moves (renaming/shadowing) once stuck on
  these genuinely unsolvable problems; `SketchValidator`'s structural gate
  caught every one. Nothing leaked through as a false "Solved."
- **`Goal dedup hits: 16`** — `ProofGoalGraph` (this cycle's Topos-backed
  goal memory) correctly recognized repeated identical stuck-goal states
  across turns and episodes, which is exactly its intended job.

### Honest before/after

| Corpus | Base-problem "solve rate" | What it actually measured |
|---|---|---|
| `FC100SolvedSet1`-derived gap-set, N=8 swarm | 15/54 (28%) | Mostly answer-key citation (§ above) |
| `FC100OpenSet1`-derived gap-set (this section) | 0/23 (0%) | The prover's actual ability to make progress on real open research problems: none, at the current tier/turn budget |

This is the number that should be cited if this pipeline's proof-search
capability is ever described publicly — not the 28% figure, which measures
corpus leakage, not capability.
