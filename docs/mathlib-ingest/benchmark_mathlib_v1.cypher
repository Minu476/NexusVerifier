// ============================================================
// Mathlib Tactic Graph Benchmark (v1)
// Requires split-aware APPLIES edges: split = train | eval
// ============================================================

// 1) Graph shape and coverage summary
MATCH (g:GoalShape)
OPTIONAL MATCH (g)-[r:APPLIES]->()
WITH g, count(r) AS branches, coalesce(sum(r.count), 0) AS support
WITH count(g) AS total_goal_shapes,
	 sum(CASE WHEN branches > 0 THEN 1 ELSE 0 END) AS covered_goal_shapes,
	 avg(branches) AS avg_branching,
	 percentileCont(branches, 0.5) AS p50_branching,
	 avg(support) AS avg_support
RETURN total_goal_shapes,
	   covered_goal_shapes,
	   round(100.0 * covered_goal_shapes / total_goal_shapes, 2) AS coverage_pct,
	   round(avg_branching, 3) AS avg_branching,
	   round(p50_branching, 3) AS p50_branching,
	   round(avg_support, 3) AS avg_support;

// 2) Global top train tactics
MATCH ()-[r:APPLIES {split: 'train'}]->()
MATCH (t:Tactic {id: r.tactic_id})
WITH t.text AS tactic, sum(r.success_sum) AS succ, sum(r.count) AS n
WHERE n >= 5
RETURN tactic,
	   n,
	   round(100.0 * succ / n, 2) AS success_pct
ORDER BY n DESC, success_pct DESC
LIMIT 10;

// 3) Held-out recommendation quality (theorem-conditioned)
// Single-pass APPLIES aggregation avoids an expensive theorem join at full scale.
MATCH ()-[a:APPLIES]->()
WITH a.theorem_id AS theorem_id,
	 a.split AS split,
	 a.tactic_id AS tactic_id,
	 sum(a.success_sum) AS succ,
	 sum(a.count) AS n
WITH theorem_id,
	 collect(CASE WHEN split = 'train' THEN {tactic_id: tactic_id, score: 1.0 * succ / n, n: n} END) AS train_raw,
	 collect(CASE WHEN split = 'eval' THEN tactic_id END) AS eval_raw
WITH theorem_id,
	 [x IN train_raw WHERE x IS NOT NULL] AS train_rows,
	 [x IN eval_raw WHERE x IS NOT NULL] AS eval_tactics
WHERE size(train_rows) > 0 AND size(eval_tactics) > 0
UNWIND train_rows AS tr
WITH theorem_id, eval_tactics, tr
ORDER BY theorem_id, tr.score DESC, tr.n DESC
WITH theorem_id,
	 eval_tactics,
	 collect(tr.tactic_id)[0..3] AS top3
WITH theorem_id,
	 eval_tactics,
	 top3[0..1] AS top1,
	 top3
WITH count(*) AS evaluated_theorems,
	 sum(CASE WHEN size([x IN top1 WHERE x IN eval_tactics]) > 0 THEN 1 ELSE 0 END) AS hit1,
	 sum(CASE WHEN size([x IN top3 WHERE x IN eval_tactics]) > 0 THEN 1 ELSE 0 END) AS hit3
RETURN evaluated_theorems,
	   hit1,
	   hit3,
	   round(1.0 * hit1 / evaluated_theorems, 4) AS hit_at_1,
	   round(1.0 * hit3 / evaluated_theorems, 4) AS hit_at_3;

// 4) Premise graph coverage
MATCH (th:Theorem)
OPTIONAL MATCH (th)-[:USES_PREMISE]->(p:Premise)
WITH count(DISTINCT th) AS theorems,
	 count(DISTINCT p) AS premises,
	 sum(CASE WHEN p IS NULL THEN 0 ELSE 1 END) AS theorem_premise_pairs
RETURN theorems,
	   premises,
	   theorem_premise_pairs;

// 5) Most reused premises
MATCH (:Theorem)-[u:USES_PREMISE]->(p:Premise)
RETURN p.name AS premise,
	   count(u) AS theorem_count
ORDER BY theorem_count DESC
LIMIT 20;
