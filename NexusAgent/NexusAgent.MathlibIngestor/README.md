# NexusAgent.MathlibIngestor

Mathlib4 graph extractor scaffold for building a deterministic state-transition graph from Lean tactic transitions.

## Input

The tool accepts `.jsonl` or `.json` transition exports, including:

- InfoTree-style records with `goal_before`, `tactic_raw`, and `goals_after`.
- LeanDojo-style records with `state_before` / `state_after` / `tactic`.

Schema contract:

- `transition_schema.json`

Lean scaffold:

- `lean/InfoTreeWalker.lean`

## Output

The tool emits Neo4j bulk-import CSVs:

- `goals_nodes.csv`
- `tactics_nodes.csv`
- `edges.csv`

And helper files:

- `import_neo4j.sh`
- `validate_graph.cypher`

## Usage

```bash
dotnet run --project NexusAgent.MathlibIngestor -- \
  --input /path/to/transitions.jsonl \
  --out /path/to/out/mathlib-graph \
  --limit 100000
```

## Graph model

- `(:GoalShape {hash, canonical_text, is_solved})`
- `(:TacticApplication {tacticId, tactic_raw, theorem_source, module_source})`
- `(:GoalShape)-[:PROPOSED_MOVE {frequency}]->(:TacticApplication)`
- `(:TacticApplication)-[:YIELDS {branch_index}]->(:GoalShape)`

## Notes

- Canonicalization is slot-based and deterministic, but still text-proxy-based.
- For full compiler-grounded equivalence, feed this tool InfoTree-derived transitions emitted from a Lean plugin/walker.
