// ============================================================
// Mathlib Tactic Graph Benchmark (v2)
// Theorem-level holdout + initial-goal evaluation
//
// Split rule (deterministic): theorem_id hex prefix
//   eval  if theorem_id[0..2) < "33"   (~20%)
//   train otherwise
//
// This measures cross-theorem generalization on initial goals:
// given a held-out theorem's initial goal hash, rank tactics from
// TRAIN theorems for that goal hash and check whether top-k contains
// the held-out theorem's observed initial tactic(s).
// ============================================================

// 1) Evaluate hit@k on held-out theorem initial goals.
MATCH ()-[a:APPLIES]->()
WITH a.theorem_id AS theorem_id,
     collect(a.goal_before_hash) AS before_hashes,
     collect(a.goal_after_hash) AS after_hashes,
     collect({goal_before_hash: a.goal_before_hash, tactic_id: a.tactic_id}) AS edges
WITH theorem_id,
     [h IN before_hashes WHERE NOT h IN after_hashes] AS initial_hashes,
     edges
WHERE substring(theorem_id, 0, 2) < '33'
UNWIND initial_hashes AS goal_hash
WITH theorem_id,
     goal_hash,
     [e IN edges WHERE e.goal_before_hash = goal_hash | e.tactic_id] AS actual_tactics
OPTIONAL MATCH ()-[tr:APPLIES {goal_before_hash: goal_hash}]->()
WHERE substring(tr.theorem_id, 0, 2) >= '33'
WITH theorem_id,
     goal_hash,
     actual_tactics,
     tr.tactic_id AS tactic_id,
     sum(tr.count) AS n,
     sum(tr.success_sum) AS succ
ORDER BY theorem_id,
         goal_hash,
         CASE WHEN n = 0 THEN 0.0 ELSE 1.0 * succ / n END DESC,
         n DESC
WITH theorem_id,
     goal_hash,
     actual_tactics,
     collect(tactic_id)[0..3] AS top3
WITH theorem_id,
     goal_hash,
     actual_tactics,
     [x IN top3 WHERE x IS NOT NULL] AS top3
WITH theorem_id,
     goal_hash,
     actual_tactics,
     top3,
     top3[0..1] AS top1,
     CASE WHEN size(top3) > 0 THEN 1 ELSE 0 END AS has_train_candidates
WITH count(*) AS eval_initial_states,
     sum(has_train_candidates) AS with_train_candidates,
     sum(CASE WHEN has_train_candidates = 0 THEN 1 ELSE 0 END) AS cold_miss_states,
     sum(CASE WHEN has_train_candidates = 1 AND size([x IN top1 WHERE x IN actual_tactics]) > 0 THEN 1 ELSE 0 END) AS hit1,
     sum(CASE WHEN has_train_candidates = 1 AND size([x IN top3 WHERE x IN actual_tactics]) > 0 THEN 1 ELSE 0 END) AS hit3
RETURN eval_initial_states,
       with_train_candidates,
       cold_miss_states,
    round(CASE WHEN eval_initial_states = 0 THEN 0.0 ELSE 1.0 * cold_miss_states / eval_initial_states END, 4) AS cold_miss_rate,
       hit1,
       hit3,
       round(CASE WHEN with_train_candidates = 0 THEN 0.0 ELSE 1.0 * hit1 / with_train_candidates END, 4) AS hit_at_1_on_covered,
       round(CASE WHEN with_train_candidates = 0 THEN 0.0 ELSE 1.0 * hit3 / with_train_candidates END, 4) AS hit_at_3_on_covered,
    round(CASE WHEN eval_initial_states = 0 THEN 0.0 ELSE 1.0 * hit1 / eval_initial_states END, 4) AS hit_at_1_overall,
    round(CASE WHEN eval_initial_states = 0 THEN 0.0 ELSE 1.0 * hit3 / eval_initial_states END, 4) AS hit_at_3_overall;

// 2) Sample hard misses for inspection.
MATCH ()-[a:APPLIES]->()
WITH a.theorem_id AS theorem_id,
     collect(a.goal_before_hash) AS before_hashes,
     collect(a.goal_after_hash) AS after_hashes,
     collect({goal_before_hash: a.goal_before_hash, tactic_id: a.tactic_id}) AS edges
WITH theorem_id,
     [h IN before_hashes WHERE NOT h IN after_hashes] AS initial_hashes,
     edges
WHERE substring(theorem_id, 0, 2) < '33'
UNWIND initial_hashes AS goal_hash
WITH theorem_id,
     goal_hash,
     [e IN edges WHERE e.goal_before_hash = goal_hash | e.tactic_id] AS actual_tactics
OPTIONAL MATCH ()-[tr:APPLIES {goal_before_hash: goal_hash}]->()
WHERE substring(tr.theorem_id, 0, 2) >= '33'
WITH theorem_id,
     goal_hash,
     actual_tactics,
     tr.tactic_id AS tactic_id,
     sum(tr.count) AS n,
     sum(tr.success_sum) AS succ
ORDER BY theorem_id,
         goal_hash,
         CASE WHEN n = 0 THEN 0.0 ELSE 1.0 * succ / n END DESC,
         n DESC
WITH theorem_id,
     goal_hash,
     actual_tactics,
     collect(tactic_id)[0..3] AS top3
WITH theorem_id,
     goal_hash,
     actual_tactics,
     [x IN top3 WHERE x IS NOT NULL] AS top3
WHERE size(top3) = 0 OR size([x IN top3 WHERE x IN actual_tactics]) = 0
RETURN theorem_id,
       goal_hash,
       actual_tactics,
       top3 AS predicted_top3,
       CASE WHEN size(top3) = 0 THEN 'cold_miss' ELSE 'covered_but_wrong' END AS miss_type
LIMIT 30;
