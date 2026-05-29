// ============================================================
// Mathlib Tactic Graph Schema (v1 pilot)
// For LeanDojo-derived CSV import into Neo4j 5.x
// ============================================================

// ---- Constraints ----
CREATE CONSTRAINT mathlib_goal_hash IF NOT EXISTS
  FOR (g:GoalShape) REQUIRE g.hash IS UNIQUE;

CREATE CONSTRAINT mathlib_tactic_id IF NOT EXISTS
  FOR (t:Tactic) REQUIRE t.id IS UNIQUE;

CREATE CONSTRAINT mathlib_theorem_id IF NOT EXISTS
  FOR (th:Theorem) REQUIRE th.id IS UNIQUE;

CREATE CONSTRAINT mathlib_premise_id IF NOT EXISTS
  FOR (p:Premise) REQUIRE p.id IS UNIQUE;

// ---- Indexes ----
CREATE INDEX mathlib_theorem_name IF NOT EXISTS
  FOR (th:Theorem) ON (th.name);

CREATE INDEX mathlib_tactic_template IF NOT EXISTS
  FOR (t:Tactic) ON (t.text);

CREATE INDEX mathlib_premise_name IF NOT EXISTS
  FOR (p:Premise) ON (p.name);

// ---- Node shape reference ----
// (:GoalShape {hash, structure_summary, sample_goal})
// (:Tactic {id, text, arity})
// (:Theorem {id, name, namespace, statement})
// (:Premise {id, name})

// ---- Relationship shape reference ----
// (:GoalShape)-[:APPLIES {
//   theorem_id: string,
//   tactic_id: string,
//   split: "train" | "eval",
//   premises_count: int,
//   success_sum: int,
//   count: int
// }]->(:GoalShape)
// (:GoalShape)-[:USES_TACTIC]->(:Tactic)
// (:Theorem)-[:CLOSES]->(:GoalShape)
// (:Theorem)-[:USES_PREMISE]->(:Premise)
