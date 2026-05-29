// ============================================================
// Erdos vector-reach benchmark (GoalShape vector index)
//
// Input CSV (served over HTTP): erdos_vectors.csv
// columns: problem_id,theorem_name,goal_text,vector
// vector format: 64 floats joined by '|'
// ============================================================

// 1) Reach summary: median top-1 cosine across Erdos sketches
LOAD CSV WITH HEADERS FROM 'http://127.0.0.1:8990/erdos_vectors.csv' AS row
WITH row.problem_id AS problem_id,
     [x IN split(row.vector, '|') | toFloat(x)] AS qvec
CALL db.index.vector.queryNodes('goalshape_state_vec_idx', 10, qvec)
YIELD node, score
WITH problem_id, max(score) AS top1
RETURN count(*) AS sketches_evaluated,
       round(percentileCont(top1, 0.50), 4) AS median_top1_cosine,
       round(avg(top1), 4) AS mean_top1_cosine,
       round(min(top1), 4) AS min_top1_cosine,
       round(max(top1), 4) AS max_top1_cosine;

// 2) Top tactics from union of each sketch's top-10 neighbors
LOAD CSV WITH HEADERS FROM 'http://127.0.0.1:8990/erdos_vectors.csv' AS row
WITH row.problem_id AS problem_id,
     [x IN split(row.vector, '|') | toFloat(x)] AS qvec
CALL db.index.vector.queryNodes('goalshape_state_vec_idx', 10, qvec)
YIELD node, score
WITH DISTINCT problem_id, node
MATCH (node)-[r:APPLIES]->()
MATCH (t:Tactic {id: r.tactic_id})
WITH t.text AS tactic,
     sum(r.count) AS support,
     count(DISTINCT problem_id) AS sketch_hits
RETURN tactic, support, sketch_hits
ORDER BY support DESC, sketch_hits DESC
LIMIT 25;

// 3) Optional per-sketch top1 inspection
LOAD CSV WITH HEADERS FROM 'http://127.0.0.1:8990/erdos_vectors.csv' AS row
WITH row.problem_id AS problem_id,
     [x IN split(row.vector, '|') | toFloat(x)] AS qvec
CALL db.index.vector.queryNodes('goalshape_state_vec_idx', 1, qvec)
YIELD node, score
RETURN problem_id,
       node.hash AS nearest_goal_hash,
       round(score, 4) AS top1_cosine
ORDER BY top1_cosine DESC
LIMIT 30;
