// ============================================================
// Erdos cold-miss diagnostic for initial goal hashes
//
// Expected input CSV (served over localhost HTTP due Neo4j Desktop file:// limits):
//   problem_id,theorem_name,hash_plain,hash_turnstile
//
// Example:
//   cd data/erdos_phase9_ams5 && python3 -m http.server 8989
//   cypher-shell ... -f docs/mathlib-ingest/benchmark_erdos_cold_miss.cypher
// ============================================================

// 1) Coverage / cold-miss rate on provided Erdos targets.
LOAD CSV WITH HEADERS FROM 'http://127.0.0.1:8989/erdos_goal_hashes.csv' AS row
WITH row.problem_id AS problem_id,
     [row.hash_plain, row.hash_turnstile] AS hash_candidates
UNWIND hash_candidates AS goal_hash
WITH problem_id, goal_hash
WHERE goal_hash IS NOT NULL AND goal_hash <> ''
OPTIONAL MATCH ()-[r:APPLIES {goal_before_hash: goal_hash}]->()
WITH problem_id,
     goal_hash,
     count(r) AS support
WITH problem_id,
     max(CASE WHEN support > 0 THEN 1 ELSE 0 END) AS has_match,
     max(support) AS best_support
WITH count(*) AS total_problems,
     sum(has_match) AS covered_problems,
     sum(CASE WHEN has_match = 0 THEN 1 ELSE 0 END) AS cold_miss_problems,
     avg(best_support) AS avg_best_support
RETURN total_problems,
       covered_problems,
       cold_miss_problems,
       round(1.0 * cold_miss_problems / total_problems, 4) AS cold_miss_rate,
       round(avg_best_support, 2) AS avg_best_support;

// 2) Per-problem tactical support from train split (top-5 by frequency).
LOAD CSV WITH HEADERS FROM 'http://127.0.0.1:8989/erdos_goal_hashes.csv' AS row
WITH row.problem_id AS problem_id,
     [row.hash_plain, row.hash_turnstile] AS hash_candidates
UNWIND hash_candidates AS goal_hash
WITH problem_id, goal_hash
WHERE goal_hash IS NOT NULL AND goal_hash <> ''
MATCH ()-[tr:APPLIES {goal_before_hash: goal_hash}]->()
WHERE substring(tr.theorem_id, 0, 2) >= '33'
MATCH (t:Tactic {id: tr.tactic_id})
WITH problem_id,
     t.text AS tactic,
     sum(tr.count) AS n,
     sum(tr.success_sum) AS succ
ORDER BY problem_id, n DESC, (1.0 * succ / n) DESC
WITH problem_id, collect({tactic: tactic, support: n})[0..5] AS top5
RETURN problem_id, top5
ORDER BY problem_id;

// 3) Explicit cold-miss list.
LOAD CSV WITH HEADERS FROM 'http://127.0.0.1:8989/erdos_goal_hashes.csv' AS row
WITH row.problem_id AS problem_id,
     [row.hash_plain, row.hash_turnstile] AS hash_candidates
UNWIND hash_candidates AS goal_hash
WITH problem_id, goal_hash
WHERE goal_hash IS NOT NULL AND goal_hash <> ''
OPTIONAL MATCH ()-[r:APPLIES {goal_before_hash: goal_hash}]->()
WITH problem_id,
     goal_hash,
     count(r) AS support
WITH problem_id,
     max(CASE WHEN support > 0 THEN 1 ELSE 0 END) AS has_match
WITH problem_id, has_match
WHERE has_match = 0
RETURN problem_id
ORDER BY problem_id;
