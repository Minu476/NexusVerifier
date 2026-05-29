// ============================================================
// Pilot import script (after CSV generation)
// Assumes CSVs are in Neo4j import dir with these names:
// goal_shapes.csv, tactics.csv, theorems.csv, applications.csv
// ============================================================

// 1) Nodes
LOAD CSV WITH HEADERS FROM 'file:///goal_shapes.csv' AS row
MERGE (g:GoalShape {hash: row.hash})
SET g.structure_summary = row.structure_summary,
    g.sample_goal = row.sample_goal;

LOAD CSV WITH HEADERS FROM 'file:///tactics.csv' AS row
MERGE (t:Tactic {id: row.id})
SET t.text = row.template,
    t.arity = toInteger(row.arity);

LOAD CSV WITH HEADERS FROM 'file:///theorems.csv' AS row
MERGE (th:Theorem {id: row.id})
SET th.name = row.name,
    th.namespace = row.namespace,
    th.statement = row.statement;

LOAD CSV WITH HEADERS FROM 'file:///premises.csv' AS row
MERGE (p:Premise {id: row.id})
SET p.name = row.name;

// 2) Edges
LOAD CSV WITH HEADERS FROM 'file:///applications.csv' AS row
MATCH (g1:GoalShape {hash: row.goal_before_hash})
MATCH (g2:GoalShape {hash: row.goal_after_hash})
MATCH (th:Theorem {id: row.theorem_id})
MATCH (t:Tactic {id: row.tactic_id})
MERGE (g1)-[r:APPLIES {
    theorem_id: row.theorem_id,
    tactic_id: row.tactic_id,
    split: row.split
}]->(g2)
ON CREATE SET r.premises_count_sum = toInteger(row.premises_count),
              r.success_sum = toInteger(row.success),
              r.count = 1
ON MATCH SET  r.premises_count_sum = r.premises_count_sum + toInteger(row.premises_count),
              r.success_sum = r.success_sum + toInteger(row.success),
              r.count = r.count + 1
MERGE (th)-[:CLOSES]->(g2)
MERGE (g1)-[:USES_TACTIC]->(t);

LOAD CSV WITH HEADERS FROM 'file:///theorem_premises.csv' AS row
MATCH (th:Theorem {id: row.theorem_id})
MATCH (p:Premise {id: row.premise_id})
MERGE (th)-[:USES_PREMISE]->(p);
