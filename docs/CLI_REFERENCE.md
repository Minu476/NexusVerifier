# CLI reference

**Date:** 2026-07-27 · **Author:** GLM-5.2 (ZCode) · **Audience:** anyone running NexusVerifier.
Source-cited catalog of every command the `nexus` binary exposes, with flags and expected outputs.

> The README emphasizes `ingest-parts` (verification) and `bench` (proof search), but the actual
> CLI has **8 commands**. This doc covers all of them. Every flag is verified against the dispatch
> table and per-command argument parsers in `NexusAgent.Cli/Program.cs` and
> `VerifiedPartsPlugin.cs`.

---

## The `nexus` binary

Built from `NexusAgent/NexusAgent.Cli/Program.cs` (top-level C# statements, no explicit `Main`).
`[verified:src=NexusAgent/NexusAgent.Cli/Program.cs]` Install via either Docker (`nexus-agent:latest`
image, see [`GETTING_STARTED.md`](GETTING_STARTED.md)) or `dotnet run --project NexusAgent.Cli`.

### Command dispatch

The CLI dispatches on the first positional argument: `[verified:src=NexusAgent/NexusAgent.Cli/Program.cs:194-206]`

| Command | Purpose | Handler |
|---|---|---|
| `solve <file>` | Active proof search on a single problem | `RunSolveAsync` (Program.cs:573) |
| `bench <dir>` | Batch proof search across a directory of problems | `RunBenchAsync` (Program.cs:595) |
| `schema` | Print the Neo4j schema (the contents of `docs/neo4j_schema.cypher`) | `RunSchema` (Program.cs:726) |
| `stats` | Fossil-vault analysis report (HTML) | `RunStatsAsync` (Program.cs:734) |
| `probe` | Environment diagnostics (no Neo4j schema ensure) | `RunProbeAsync` (Program.cs:461) |
| `ingest-hg` | Ingest Lean hypergraph edges from JSONL into Neo4j | `RunIngestHgAsync` (Program.cs:422) |
| `scan-hg` | Scan Mathlib for hypergraph edges via Lean elaboration | `RunScanHgAsync` (Program.cs:218) |
| `ingest-parts` | Verify a JSON file of Lean proof parts (the verification mode) | `VerifiedPartsPlugin.RunCommandAsync` (Program.cs:203) |

### Global behavior

- **Schema auto-ensure:** every command except `probe` calls `EnsureSchemaAsync` on the Neo4j
  client at startup, creating the constraints and vector indexes if they don't exist.
  `[verified:src=NexusAgent/NexusAgent.Cli/Program.cs:184-189]`
- **Logging:** Serilog to `logs/nexus-<date>.log` next to the binary, daily-rolled, 7 retained.
  `[verified:src=NexusAgent/NexusAgent.Cli/Program.cs:35-56]`
- **Exit codes:** `ingest-parts` exits `1` if any part was rejected, else `0`.
  `[verified:src=NexusAgent/NexusAgent.VerifiedParts/VerifiedPartsPlugin.cs:205]` Other commands
  exit `0` on completion.

---

## Verification mode

### `ingest-parts` — verify Lean proof parts

The canonical verification command. Loads a JSON file of `VerifiedPart` records and runs each
through the four-gate pipeline (compile → axiom → scope → sink fan-out). See
[`ARCHITECTURE.md`](ARCHITECTURE.md) for the gate sequence.

```bash
nexus ingest-parts \
  --from-json parts.json \
  --dry-run \
  --native-decide reject \
  --sinks fossil \
  --exclude-targets /tmp/fc100_targets.txt
```

`[verified:src=NexusAgent/NexusAgent.VerifiedParts/VerifiedPartsPlugin.cs:65-130]`

#### Flags

| Flag | Default | Purpose | Source |
|---|---|---|---|
| `--from-json <path>` | (required) | JSON file of `VerifiedPart[]` to verify | VerifiedPartsPlugin.cs:70 |
| `--dry-run` | off | Run the gates (compile, axiom, scope) but skip sink writes — no Neo4j mutations | VerifiedPartsPlugin.cs:71 |
| `--native-decide reject\|flag` | from `NEXUS_PARTS_NATIVE_DECIDE` env (default `reject`) | `reject` skips parts using `decide +native`; `flag` accepts but tags them. **Note: not `warn` or `allow`** — see `[CONFIGURATION.md](CONFIGURATION.md)`. | VerifiedPartsPlugin.cs:72, 87-88 |
| `--sinks fossil,landmark` | `fossil` | Which sinks to fan out to on a successful verify. `fossil` → `:ProofFossil`; `landmark` → `:ProofLandmark` + transition. | VerifiedPartsPlugin.cs:73, 91-93 |
| `--exclude-targets <path>` | (none) | Newline-separated declaration names to hold out. Triggers **parent-problem-level** exclusion: siblings of any listed declaration are also excluded. | VerifiedPartsPlugin.cs:74, 96-116 |

#### Input format (`VerifiedPart`)

```json
[
  {
    "partName": "erdos_647.variants.twenty_four",
    "problemId": "erdos_647",
    "domainTag": "Erdos",
    "statementText": "...",
    "proofBlock": "...",
    "importsHeader": "import Mathlib.Data.Nat.Basic\n...",
    "scope": "Weaker",
    "fullScopeConfirmed": false,
    "isHeldOut": true,
    "source": "FC100SolvedSet1"
  }
]
```

`[verified:src=NexusAgent/NexusAgent.VerifiedParts/Models/VerifiedPart.cs:13 — record with these fields]`
`[verified:src=parts.json — a real 10-entry example, the file the README's Quickstart uses]`

`Scope` is one of `Unknown` (default), `Weaker`, `Instance`, `Part`, `Full`. `Full` is blocked
unless `FullScopeConfirmed=true`. `[verified:src=NexusAgent/NexusAgent.VerifiedParts/Models/PartScope.cs:7]`
`[verified:src=NexusAgent/NexusAgent.VerifiedParts/VerifiedPartIngestor.cs:99]`

#### Expected output

```
[ingest-parts] 10 parts from parts.json  sinks=[fossil]  (DRY RUN — no writes)
  FAIL  erdos_1148.variants.lower_bound  — native axioms (policy=reject): [...]
  PASS  [Weaker] erdos_647.variants.twenty_four  axioms=[propext, Classical.choice, Quot.sound]
  ...
[ingest-parts] Done: 7 passed, 3 rejected, 0 excluded (holdout).
```

`[verified:src=README.md:218-224]` The gate sequence per part: compile → axiom profile → scope
guard → sink fan-out. `[verified:src=NexusAgent/NexusAgent.VerifiedParts/VerifiedPartIngestor.cs:71-131]`

---

## Proof-search mode

### `solve` — single-problem proof search

```bash
nexus solve path/to/problem.lean \
  --id erdos_228 \
  --domain Erdos \
  --statement "forall (f : ...), ..."
```

`[verified:src=NexusAgent/NexusAgent.Cli/Program.cs:573, 581-583]`

#### Flags

| Flag | Purpose | Source |
|---|---|---|
| `--id <string>` | Problem identifier (e.g. `erdos_228`) | Program.cs:581 |
| `--domain <string>` | Domain tag (`Erdos`, `OEIS`, ...) | Program.cs:582 |
| `--statement <string>` | Theorem statement text | Program.cs:583 |

Behavior: constructs a `ProblemInput` and calls `NexusOrchestrator.SolveAsync`. Runs the per-episode
tier pipeline (graph replay → graph-native → fossil → hallucination scan → LLM → compile →
structural gate → record). See [`ARCHITECTURE.md`](ARCHITECTURE.md).

### `bench` — batch proof search across a directory

```bash
nexus bench data/erdos_phase8/ \
  --source Erdos \
  --max-episodes 3 \
  --max-turns 8 \
  --parallel 4 \
  --fossil-match-threshold 0.75 \
  --graph-first
```

`[verified:src=NexusAgent/NexusAgent.Cli/Program.cs:595, 603-621]`

#### Flags

| Flag | Default | Purpose | Source |
|---|---|---|---|
| `--source OEIS\|Erdos` | (required) | Corpus family | Program.cs:603 |
| `--max-episodes <int>` | 3 | Episodes per problem | Program.cs:604 |
| `--max-turns <int>` | 8 | Turns per episode | Program.cs:605 |
| `--parallel <int>` | 4 | Concurrent problems (each is serial internally) | Program.cs:606 |
| `--fossil-match-threshold <float>` | 0.75 | Cosine similarity for a fossil-vault hit | Program.cs:607 |
| `--fossil-direct-threshold <float>` | 0.70 | Threshold for direct fossil substitution | Program.cs:608 |
| `--graph-first` | off | Use the BestFirstGraphPlanner before LLM fallback | Program.cs:609 |
| `--no-llm-fallback` | off | Disable the legacy LLM prover | Program.cs:610 |
| `--planner-max-expansions <int>` | 48 | Planner candidate cap | Program.cs:611 |
| `--planner-branch-factor <int>` | 8 | Branching factor | Program.cs:612 |
| `--planner-neighbor-k <int>` | 12 | Neighbor count for retrieval | Program.cs:613 |
| `--planner-state-visit-cap <int>` | 3 | Re-visit cap per state | Program.cs:614 |
| `--planner-{depth,rank,success,branching,error}-weight <float>` | varies | Planner scoring weights | Program.cs:615-619 |
| `--planner-novelty-bonus <float>` | varies | Novelty reward | Program.cs:620 |

#### Output

`bench` writes two artifacts to `<dir>/../results/`: `bench-<timestamp>.json` (full per-problem
telemetry) and `bench-<timestamp>.html` (human-readable report). `[verified:src=NexusAgent/NexusAgent.Cli/Program.cs:711-715]`

> **Honest expectation:** against `FC100OpenSet1` (the actual open-problems corpus), `bench` solves
> 0/23. The earlier 7/10 and 43/66 figures were against `FC100SolvedSet1` (already-solved problems)
> positioned as proof-search tests — a corpus-selection error since corrected.
> `[verified:src=README.md:62-72]` `[verified:docs=docs/FC100_GAP_SET_METHODOLOGY.md]`

---

## Hypergraph extraction

### `scan-hg` — scan Mathlib for hypergraph edges via Lean elaboration

```bash
nexus scan-hg \
  --shards 8 \
  --timeout-minutes 5 \
  --holdout
```

`[verified:src=NexusAgent/NexusAgent.Cli/Program.cs:218, 229-231]`

Walks Mathlib declarations via the Lean elaborator (using `ErdosHypergraph.lean`, which lives in
the `formal-conjectures` checkout), extracts AND-OR hypergraph edges (function + inputs + output),
and upserts an `:HgScanRun` node into Neo4j tracking the run. `[verified:src=NexusAgent/NexusAgent.Cli/Program.cs:408-409]`

#### Flags

| Flag | Default | Purpose | Source |
|---|---|---|---|
| `--shards <int>` | 8 | Parallel Lean shards | Program.cs:229 |
| `--timeout-minutes <int>` | 5 | Per-shard timeout | Program.cs:230 |
| `--holdout` | off | Apply holdout isolation to the scan | Program.cs:231 |

The command also embeds a hardcoded list of 100 FC100 goal names (the canonical FC100 set, with a
comment that the order must match `fc100Decls` in `ErdosHypergraph.lean`).
`[verified:src=NexusAgent/NexusAgent.Cli/Program.cs:238-340]`

### `ingest-hg` — ingest pre-extracted hypergraph edges from JSONL

```bash
nexus ingest-hg \
  --input formal-conjectures/_nexus_tmp/hg_cache.jsonl
```

`[verified:src=NexusAgent/NexusAgent.Cli/Program.cs:422, 429-430]`

Reads JSONL hypergraph edges (the output of `scan-hg` or the Lean-native hypergraph engine
described in [`hypergraph-engine.md`](hypergraph-engine.md)) and ingests them into Neo4j.

#### Flags

| Flag | Default | Purpose | Source |
|---|---|---|---|
| `--input <path>` | `formal-conjectures/_nexus_tmp/hg_cache.jsonl` | JSONL edge file | Program.cs:429-430 |

---

## Diagnostics & reporting

### `probe` — environment diagnostics

```bash
nexus probe
```

`[verified:src=NexusAgent/NexusAgent.Cli/Program.cs:461]` The only command that does **not** call
`EnsureSchemaAsync` — safe to run against a Neo4j instance you don't want mutated.
`[verified:src=NexusAgent/NexusAgent.Cli/Program.cs:184-189]` Reports: Neo4j connectivity, Lean
toolchain version, configured LLM providers, Ollama models (from `OLLAMA_MODELS`).
`[verified:src=NexusAgent/NexusAgent.Cli/Program.cs:476]`

### `schema` — print the Neo4j schema

```bash
nexus schema
```

`[verified:src=NexusAgent/NexusAgent.Cli/Program.cs:726-732]` Prints the contents of
`docs/neo4j_schema.cypher` to stdout. Use this to see the constraints and vector indexes the
pipeline expects.

### `stats` — fossil-vault analysis report

```bash
nexus stats
```

`[verified:src=NexusAgent/NexusAgent.Cli/Program.cs:734]` Generates an HTML analysis of the fossil
vault (`:ProofFossil` node store) — coverage, use counts, domain distribution. Writes to
`<baseDir>/../../../data/results/fossil-analysis-<timestamp>.html`.
`[verified:src=NexusAgent/NexusAgent.Cli/Program.cs:793-796]`

---

## The other two CLIs (not the `nexus` binary)

NexusVerifier ships two additional `dotnet run`-able tools that are not part of the `nexus`
command surface:

### `NexusAgent.MathlibIngestor` — offline Mathlib tactic-graph builder

```bash
dotnet run --project NexusAgent/NexusAgent.MathlibIngestor -- \
  --input path/to/leandojo.jsonl \
  --out out/ \
  --limit 10000
```

`[verified:src=NexusAgent/NexusAgent.MathlibIngestor/Program.cs:9-11, 79-88]` Reads LeanDojo
JSON/JSONL, canonicalizes goals, and emits Neo4j bulk-import CSVs (`goals_nodes.csv`,
`tactics_nodes.csv`, `edges.csv`) plus `import_neo4j.sh` and `validate_graph.cypher`. Uses
`neo4j-admin database import full` with node labels `GoalShape` and `TacticApplication` and
relationship types `PROPOSED_MOVE`, `YIELDS` — **different labels from the runtime schema**,
because this builds a separate offline analysis graph. `[verified:src=NexusAgent/NexusAgent.MathlibIngestor/Program.cs:106-119]`

### `NexusAgent.ToposExperiment` — the Topos integration research harness

Not user-facing. The experimental harness from `docs/TOPOS_INTEGRATION_REPORT.md` — runs the
AND-OR backward chainer over a Topos `HypergraphKernel`. `[verified:src=NexusAgent/NexusAgent.ToposExperiment/Program.cs]`

---

## Common patterns

### "Verify my environment works"

```bash
nexus probe                                       # no Neo4j writes; checks connectivity
nexus ingest-parts --from-json parts.json --dry-run    # full pipeline minus writes
```

### "Re-verify the FC100 sample after a pipeline change"

```bash
nexus ingest-parts --from-json parts.json --dry-run
# expect: 7 passed, 3 rejected, 0 excluded (holdout)
```

`[verified:src=README.md:118 — this exact gate was re-verified 2026-07-26 against the fixed pipeline]`

### "Verify with holdout isolation"

```bash
echo "Erdos1074.erdos_1074.variants.EHSNumbers_init" > /tmp/fc100_targets.txt
nexus ingest-parts --from-json parts.json \
     --exclude-targets /tmp/fc100_targets.txt \
     --dry-run
# expect: 6 passed, 3 rejected, 1 excluded (holdout)
```

`[verified:src=README.md:226-246]` Excluding `Erdos1074.erdos_1074.variants.EHSNumbers_init`
automatically excludes its sibling `erdos_1074.variants.mem_pillaiPrimes` (same parent
`erdos1074`).

### "Run a proof-search benchmark"

```bash
nexus bench data/erdos_phase8/ --source Erdos --max-episodes 3 --parallel 4
# results written to data/results/bench-<timestamp>.{json,html}
```

---

## Cross-references

- **Configuration knobs (the full env-var surface)** → [`CONFIGURATION.md`](CONFIGURATION.md).
- **The system's internal architecture** → [`ARCHITECTURE.md`](ARCHITECTURE.md).
- **The verification gate sequence** → [`ARCHITECTURE.md`](ARCHITECTURE.md) §"The four-gate pipeline".
- **The proof-search tier pipeline** → [`ARCHITECTURE.md`](ARCHITECTURE.md) §"The per-episode turn loop".
