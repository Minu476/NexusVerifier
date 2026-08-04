# Swarm Run: Solvable-Tier Corpus — Results

**Run:** 2026-08-03 09:27–09:58 (~31 min wall-clock)
**Models:** deepseek-v4-flash (Tier 1/2), deepseek-v4-pro (Tier 3)
**Config:** 31 stubs · 12-way parallel · 3 episodes · 10 turns/episode · $200 cap · graph-first planner
**Spend:** **$0.55** of $200 budget

## Headline (honest, audited)

| | Count | % |
|---|---|---|
| Problems attempted | 31 | — |
| Raw solves (Lean accepts, axiom-clean) | 17 | 55% |
| **Independent solves (citation-free)** | **13** | **42%** |
| Citation solves (answer-key leak) | 4 | 13% |
| Aborted (no progress in N episodes) | 12 | 39% |
| Episode budget exhausted | 2 | 6% |

**The number that reflects the prover's actual proof-search ability: 13/31 independent solves (42%).**

The 4 citation solves (`exact <original_declaration>`) are technically valid, axiom-clean
proofs — but they don't constitute proof search. They exploit the fact that importing the
type definitions also brings the original complete proofs into scope. This is the same
leakage mechanism documented in `docs/FC100_GAP_SET_METHODOLOGY.md`. The citation audit
(`scripts/citation_audit.py`) catches what `HasSorryAxiomAsync` cannot.

## The 13 independent solves

| Problem | Original domain | Notes |
|---|---|---|
| `a_0` (OeisA280831) | OEIS / number theory | |
| `a_65` (OeisA56777) | OEIS / number theory | |
| `boundaryCount_univ` (Mathoverflow10799) | combinatorics | |
| `congruentNumber_7` | number theory | |
| `count_false_morphism` (OeisA6697) | OEIS | |
| `eventually_palindrome_base10` (Lychrel) | number theory | |
| `hasConstantOverlapSq_singleton` | quantum | |
| `hasGap_empty` (Green32) | additive combinatorics | |
| `isUnitaryPerfect_60` | algebra | |
| `lcmInterval_lt_example3` (Erdos678) | number theory | |
| `pellNumber_two` | number theory | `rfl`-class |
| `sicOverlapSq_three` | quantum | |
| `star_smul_mul_smul` (OpenQuantumProblem13) | quantum | solved in episode 2 after 2 failed episodes |

## The 4 citation solves (not independent)

| Problem | Winning tactic | What it actually did |
|---|---|---|
| `ame_2_exists` | `exact ame_2_exists hd` | cited the original |
| `eqSystem4_has_solution_d2` | `exact eqSystem4_has_solution_d2 (α := α)` | cited the original |
| `flt_of_beal_conjecture` | `exact flt_of_beal_conjecture H` | cited the original |
| `isClusterPrime_97_isLeast_non_cluster` | `exact isClusterPrime_97...` | cited the original |

## What worked

- **The structural-gate caught reward hacking.** 8 structural rejections across the run —
  DeepSeek repeatedly tried to rename/restate theorems to fake a solve; `SketchValidator`
  caught every one. No false "Solved" leaked.
- **Goal-dedup fired 82 times** — the Topos goal-graph correctly recognized repeated stuck
  states, avoiding wasted re-exploration.
- **Fossil retrieval surfaced near-misses.** Several failed problems hit fossil sims of
  0.99–1.000 (e.g. `f_undefined_at_2`, `firstCol_normSq`) but below the substitute
  threshold — the encoder is *finding* the right lemma but not *using* it. This is a
  threshold-tuning opportunity, not a capability gap.
- **Tier escalation worked.** v4-pro engaged on hard problems (Tier 3); the tier-ceiling
  demotion correctly kicked in after structural violations.

## What didn't work

- **Graph-native proposer (Tier 0.75) was offline** — the `goalshape_state_vec_idx`
  vector index wasn't populated (requires the offline Mathlib ingest). 0 proposals
  attempted. This is the deterministic-tactic-first tier that should be the cheapest win;
  it was inert this run.
- **Planner accepted 0 transitions** (31 runs, 31 expansions, 0 accepts) — the offline
  goal-shape graph has no content to propose from on a fresh database.
- **Fossil conversion 0%** (0/53 retrievals led to direct substitution) — the retrieval
  threshold (0.75 match / 0.70 direct-sub) may be too high; near-misses at 0.99 didn't
  substitute.
- **The 12 aborts** burned the most budget per problem ($0.02–0.04 each) on problems where
  DeepSeek couldn't find *any* sorry reduction across 2–3 episodes of 10–20 turns.

## Comparison to prior runs

| Corpus | Method | Solve rate | What it measured |
|---|---|---|---|
| FC100SolvedSet1 gap-set, N=8 swarm | brute sampling | 15/54 (28%) | Mostly answer-key citation (documented) |
| FC100OpenSet1 gap-set | single try | 0/23 (0%) | Real open-problem capability: none |
| **This run: solvable-tier stubs** | **swarm + audit** | **13/31 (42%) independent** | **Proof reconstruction on textbook/API-tier decls** |

## Cost efficiency

$0.55 for 13 independent solves = **~4.2 cents per independent solve**. The 4 citation
solves cost ~$0.01 total (1–2 turn `exact` calls). The 12 aborts cost the bulk (~$0.40)
at ~3.3 cents each for no result — they're the obvious target for cost optimization
(earlier abort, lower tier ceiling on clearly-stuck problems).
