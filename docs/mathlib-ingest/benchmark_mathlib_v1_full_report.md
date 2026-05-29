# Mathlib v1 Full Benchmark Report

- Timestamp: 2026-05-27 12:29:29 PDT
- Database: `nexusdb`
- Source dataset: full ingestion (post-fallback import)

## Section 1: Graph coverage and branching

| total_goal_shapes | covered_goal_shapes | coverage_pct | avg_branching | p50_branching | avg_support |
|---:|---:|---:|---:|---:|---:|
| 267362 | 249015 | 93.14 | 0.938 | 1.0 | 0.938 |

- Query engine timing (cypher-shell): ready in 1 ms, consumed in 146 ms.

## Section 2: Top train tactics

| tactic | n | success_pct |
|---|---:|---:|
| simp | 5015 | 100.0 |
| rfl | 3008 | 100.0 |
| ext | 1498 | 100.0 |
| constructor | 1488 | 100.0 |
| ring | 717 | 100.0 |
| congr | 700 | 100.0 |
| ext x | 592 | 100.0 |
| intro h | 584 | 100.0 |
| omega | 537 | 100.0 |
| positivity | 513 | 100.0 |

- Query engine timing (cypher-shell): ready in 1 ms, consumed in 697 ms.

## Section 3: Held-out recommendation quality (theorem-conditioned)

This was executed using an optimized, semantically equivalent query to avoid a long-running join pattern on full scale.

| evaluated_theorems | hit1 | hit3 | hit_at_1 | hit_at_3 |
|---:|---:|---:|---:|---:|
| 22406 | 453 | 806 | 0.0202 | 0.0360 |

- Query engine timing (cypher-shell): ready in 0 ms, consumed in 473 ms.
- Wall time (`/usr/bin/time -p`): real 1.43s, user 1.41s, sys 0.13s.

## Section 4: Premise coverage

| theorems | premises | theorem_premise_pairs |
|---:|---:|---:|
| 59555 | 65506 | 299627 |

- Query engine timing (cypher-shell): ready in 0 ms, consumed in 48 ms.

## Section 5: Top reused premises

| premise | theorem_count |
|---|---:|
| rfl | 2564 |
| Eq.symm | 1443 |
| mul_comm | 1417 |
| mul_assoc | 1227 |
| mul_one | 1131 |
| one_mul | 973 |
| add_comm | 918 |
| le_antisymm | 793 |
| LE.le.trans | 679 |
| eq_comm | 678 |
| CategoryTheory.Category.assoc | 675 |
| Function.comp_apply | 669 |
| congr_arg | 658 |
| MulZeroClass.mul_zero | 626 |
| Iff.mp | 624 |
| zero_add | 613 |
| add_zero | 603 |
| Ne | 603 |
| le_rfl | 584 |
| Set | 581 |

- Query engine timing (cypher-shell): ready in 0 ms, consumed in 134 ms.
- Combined sections 4+5 wall time (`/usr/bin/time -p`): real 1.10s, user 1.41s, sys 0.12s.
