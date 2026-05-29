# DeepMind Nexus Challenge

Graph-augmented formal proof verification pipeline, evaluated against the
[DeepMind Formal Conjectures benchmark](https://github.com/google-deepmind/formal-conjectures)
(arXiv:2605.22763, May 2026).

## What this is

This repository implements **NexusAgent** — a C#/.NET 10 pipeline that:

1. Takes a set of Lean 4 proof sketches (`VerifiedPart`s) and verifies them
   rigorously using `lake env lean` + `#print axioms`.
2. Filters proofs that depend on native compilation axioms (`Lean.ofReduceBool`,
   `Lean.trustCompiler`) — indicating unverified `decide +native` tactics.
3. Maintains a Neo4j knowledge graph of proved sub-goals (the "fossil vault") for
   future reuse.
4. Enforces a holdout protocol: when a benchmark target is excluded, all
   sibling declarations from the same parent problem are automatically excluded
   too (preventing data-leakage from the same file).

## Benchmark results — FC100 (`ingest-parts` dry-run)

Evaluated on 10 sampled parts from the
[FC100SolvedSet1](https://github.com/google-deepmind/formal-conjectures) corpus
(a set of 100 non-open problems with known sorry-free Lean 4 proofs):

| Result | Count | Meaning |
|--------|-------|---------|
| **PASS [Weaker]** | 7 | Proof verified; axioms ⊆ {propext, Classical.choice, Quot.sound} |
| FAIL | 3 | Rejected — proof uses native compilation (`decide +native`) |
| EXCL | 0 (1 with holdout) | Excluded — sibling of a held-out benchmark target |

Holdout test: excluding `Erdos1074.erdos_1074.variants.EHSNumbers_init` causes
`erdos_1074.variants.mem_pillaiPrimes` (same parent `erdos1074`) to be
automatically excluded — confirmed `6 passed, 3 rejected, 1 excluded`.

> **Note:** FC100SolvedSet1 is a corpus of *already-solved, non-open* problems.
> These are known mathematical results; this pipeline verifies them mechanically
> and filters by axiom strength. It does not claim novel mathematical discoveries.

## Stack

| Layer | Technology |
|-------|-----------|
| Formal language | Lean 4 (v4.27.0) + Mathlib |
| Proof verification | `lake env lean` + `#print axioms` |
| Graph backend | Neo4j Enterprise (bolt) |
| Agent pipeline | C# / .NET 10 |
| Tests | 96 unit tests (xUnit) |
| Container | Docker (multi-stage, ~1.6 GB) |

## Quickstart (Docker)

### Prerequisites

```bash
# 1. Clone and build the formal-conjectures project (downloads ~1 GB Mathlib)
git clone https://github.com/google-deepmind/formal-conjectures
cd formal-conjectures
lake update && lake build        # takes ~20 min on first run
cd ..

# 2. Start Neo4j (or use an existing instance)
#    The compose file starts a local Neo4j 5 with APOC
cp .env.example .env
# Edit .env — set NEO4J_PASSWORD and optionally LLM API keys

# 3. Build the NexusAgent image
docker build -t nexus-agent:latest .
```

### Run a dry-run verification

```bash
# Replace the paths and credentials with your own values.
# Use a snapshot database name (not your live DB) for safety.

docker run --rm \
  -v /path/to/formal-conjectures:/formal-conjectures \
  -v "$(pwd)":/workspace:ro \
  -e NEXUS_NEO4J_URI=bolt://host.docker.internal:7687 \
  -e NEXUS_NEO4J_PASSWORD=your_password \
  -e NEXUS_NEO4J_DATABASE=nexusdb-snapshot \
  -e NEXUS_LEAN_PROJECT=/formal-conjectures \
  -e NEXUS_PARTS_NATIVE_DECIDE=reject \
  nexus-agent:latest \
  ingest-parts --from-json /workspace/parts.json --dry-run
```

Expected output:
```
[ingest-parts] 10 parts from parts.json  sinks=[fossil]  (DRY RUN — no writes)
  FAIL  erdos_1148.variants.lower_bound  — native axioms (policy=reject): [...]
  PASS  [Weaker] erdos_647.variants.twenty_four  axioms=[propext, ...]
  ...
[ingest-parts] Done: 7 passed, 3 rejected, 0 excluded (holdout).
```

### With holdout exclusion

```bash
echo "Erdos1074.erdos_1074.variants.EHSNumbers_init" > /tmp/fc100_targets.txt

docker run --rm \
  -v /path/to/formal-conjectures:/formal-conjectures \
  -v "$(pwd)":/workspace:ro \
  -v /tmp/fc100_targets.txt:/tmp/fc100_targets.txt:ro \
  -e NEXUS_NEO4J_URI=bolt://host.docker.internal:7687 \
  -e NEXUS_NEO4J_PASSWORD=your_password \
  -e NEXUS_NEO4J_DATABASE=nexusdb-snapshot \
  -e NEXUS_LEAN_PROJECT=/formal-conjectures \
  -e NEXUS_PARTS_NATIVE_DECIDE=reject \
  nexus-agent:latest \
  ingest-parts --from-json /workspace/parts.json \
               --exclude-targets /tmp/fc100_targets.txt \
               --dry-run
```

Expected: `Done: 6 passed, 3 rejected, 1 excluded (holdout).`

## Environment variables

| Variable | Default | Description |
|----------|---------|-------------|
| `NEXUS_NEO4J_URI` | `bolt://localhost:7687` | Neo4j Bolt URI |
| `NEXUS_NEO4J_USER` | `neo4j` | Neo4j user |
| `NEXUS_NEO4J_PASSWORD` | _(empty)_ | Neo4j password |
| `NEXUS_NEO4J_DATABASE` | `neo4j` | Database name |
| `NEXUS_LEAN_PROJECT` | _(required)_ | Path to built `formal-conjectures` checkout |
| `NEXUS_PARTS_NATIVE_DECIDE` | `warn` | `reject` to fail proofs using `decide +native` |
| `GOOGLE_API_KEY` | _(optional)_ | For Gemini hallucination gate |
| `DASHSCOPE_API_KEY` | _(optional)_ | For Qwen cloud gate |

## Building natively (.NET 10)

```bash
cd NexusAgent
dotnet build NexusAgent.sln

# Run all unit tests (96 tests, no external deps)
dotnet test NexusAgent.Tests/NexusAgent.Tests.csproj \
  --filter "Category!=Integration" -v q
```

## Project layout

```
NexusAgent/
  NexusAgent.Core/         # Configuration, Neo4j client, LeanOracle, planning
  NexusAgent.Cli/          # CLI entry point (ingest-parts, bench, …)
  NexusAgent.VerifiedParts/ # AxiomChecker, VerifiedPartIngestor, holdout logic
  NexusAgent.MathlibIngestor/ # Mathlib tactic graph ingestor
  NexusAgent.Tests/        # 96 unit tests (xUnit)
formal-conjectures/        # Git submodule — google-deepmind/formal-conjectures
data/                      # Benchmark input files
docs/                      # Architecture notes
```

## License

Apache 2.0. See [LICENSE](LICENSE).

The `formal-conjectures/` directory is governed by its own license
([Apache 2.0](https://github.com/google-deepmind/formal-conjectures/blob/main/LICENSE)).

  (vs. ~$1,600 equivalent at Gemini Pro rates)
