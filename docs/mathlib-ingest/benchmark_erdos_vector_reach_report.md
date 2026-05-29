# Erdos Vector Reach Report (Proxy, text-based)

- Timestamp: 2026-05-27
- Database: `nexusdb`
- GoalShape vectors backfilled to property: `GoalShape.stateVector`
- Vector index: `goalshape_state_vec_idx` (`64` dims, cosine, ONLINE)

## Method

1. Reconstructed Mathlib goal text from LeanDojo traced tactics in:
   `/Volumes/WD-Black/LeanDojo/benchmark4/leandojo_benchmark_4/random/train.json`
2. Canonicalized goals with the same rules as ingestor (`whitespace`, `forall` binder replacement, `∃` binder replacement).
3. Built vectors using the existing `ProofStateEncoder` with symmetric state construction on both sides:
   - `PendingGoals = [goal_text]`
   - `Hypotheses = []`
   - `TacticHistory = []`
   - `SorryCount = 0`
   - `ErrorMessages = []`
   - `DomainTag = "other"`
4. Backfilled vectors for GoalShape hashes present in reconstructed train data.
5. Encoded Erdos theorem statements from `data/erdos_phase9_ams5/*.lean` using the same construction.
6. Queried top-k nearest GoalShape vectors via Neo4j vector index.

## Coverage of vector backfill

- `GoalShape` total nodes: `267362`
- with `stateVector`: `249015`
- without `stateVector`: `18347`

Note: vectors are available for covered goal hashes only (consistent with prior graph coverage).

## Requested metrics

### 1) Median top-1 cosine similarity across Erdos sketches

- sketches evaluated: `70`
- median top-1 cosine: `0.9720`
- mean top-1 cosine: `0.9728`
- min top-1 cosine: `0.9487`
- max top-1 cosine: `0.9932`

### 2) Top-tactic histogram from union of each sketch's top-10 neighbors

Top rows:

| tactic | support | sketch_hits |
|---|---:|---:|
| simp | 14 | 12 |
| rfl | 7 | 6 |
| ring | 7 | 6 |
| norm_num | 5 | 5 |
| constructor | 5 | 5 |
| gcongr | 5 | 4 |
| positivity | 3 | 3 |
| omega | 3 | 3 |
| linarith | 3 | 3 |
| ext x | 3 | 3 |
| apply le_antisymm | 3 | 3 |
| ext | 3 | 3 |

## Important caveat

This run is a **proxy reach diagnostic** using theorem-statement text for Erdos goals, not elaborated initial Lean goal states. It is useful for fast viability triage, but not a final substitute for a true elaborated-goal diagnostic.

## Artifacts

- Vector generator tool:
  `NexusAgent/NexusAgent.Tools.GoalVectors/Program.cs`
- Reach benchmark query:
  `docs/mathlib-ingest/benchmark_erdos_vector_reach.cypher`
- Generated Erdos vectors:
  `data/erdos_phase9_ams5/erdos_vectors.csv`
- Generated GoalShape vectors:
  `/Volumes/WD-Black/LeanDojo/full-output-random-train-v1/goalshape_vectors.csv`
