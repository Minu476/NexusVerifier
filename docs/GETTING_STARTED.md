# Getting started with NexusVerifier

**Date:** 2026-07-27 · **Author:** GLM-5.2 (ZCode) · **Audience:** a developer or researcher who
has read [`CONCEPTS.md`](CONCEPTS.md) and wants a working environment. Honest about setup cost —
NexusVerifier is research infrastructure (Lean + Mathlib + Neo4j + Docker), not a drop-in library,
and the first run is genuinely ~30 minutes of which ~20 is building Mathlib.

> Every command, path, and expected output here is source-verified against NexusVerifier's actual
> files (not just the README's framing). Where the README and the source disagree, this doc follows
> the source and notes the discrepancy. `[verified:src=...]` tags throughout.

---

## 0. What you need before you start

| Component | Why | Verified |
|---|---|---|
| **Docker Desktop 4.x** (macOS arm64 or Linux equivalent) | The recommended path runs the whole pipeline in a container (~1.6 GB image with Lean + Mathlib baked in). | `[verified:src=README.md:175]` |
| **A Neo4j 5.x instance** (Community via Docker is fine; the project uses Enterprise locally) | The persistent graph backend — fossil vault, landmark topology, compile cache. Reachable at a Bolt URI. | `[verified:src=README.md:163]` |
| **A clone of `google-deepmind/formal-conjectures`** (~1 GB after Mathlib downloads) | The Lean project NexusVerifier verifies *against*. **Not** the same as NexusVerifier itself. | `[verified:src=README.md:188-192]` |
| **.NET 10 SDK** (only if building natively, not via Docker) | For `dotnet build NexusAgent.sln` and `dotnet test`. | `[verified:src=README.md:335-345]` |
| **(Optional) LLM API keys** | Only for proof-search mode (`solve`/`bench`). Verification mode (`ingest-parts`) needs none. | `[verified:src=.env.example:26-49]` |

> **Mode-specific minimum:** If you only want to *verify* existing Lean proofs (the lower-stakes
> mode), you need Lean + Neo4j but no LLM keys. If you want to *search for proofs*, add at least one
> LLM provider (DeepSeek, Gemini, DashScope/Qwen, or local Ollama).
> `[verified:src=NexusAgent/NexusAgent.Core/Llm/ILlmClient.cs — at least one ILlmClient implementation must be registered for solve/bench]`

---

## 1. Clone NexusVerifier (with submodules)

NexusVerifier vendors [Topos](https://github.com/Minu476/Topos) as a git submodule at
`external/Topos` — it backs the in-process `ProofGoalGraph` and the AND-OR backward chainer.
`[verified:src=.gitmodules]` `[verified:src=README.md:135-137]` Either clone recursively or
initialize after the fact:

```bash
# Option A: recursive clone
git clone --recurse-submodules https://github.com/Minu476/NexusVerifier

# Option B: if already cloned
cd NexusVerifier
git submodule update --init --recursive
```

`[verified:src=README.md:184-186]` Confirm `external/Topos/src/` is populated afterward — an empty
`external/Topos/` will cause C# build failures (the `NexusAgent.Core` project references it).

---

## 2. Build `formal-conjectures` separately (~20 minutes)

**Critical conceptual point:** NexusVerifier does *not* build its own Lean code to verify proofs.
It runs `lake env lean` against a *separate* `formal-conjectures` checkout, which is where the
Lean problem files (`OEIS/A123456.lean`, `ErdosProblems/«1148».lean`, etc.) live.
`[verified:src=NexusAgent/NexusAgent.Core/Oracle/LeanOracle.cs:151 — shells out to `lake env lean` against NEXUS_LEAN_PROJECT]`

```bash
git clone https://github.com/google-deepmind/formal-conjectures
cd formal-conjectures
lake update && lake build        # ~20 min on first run; downloads ~1 GB Mathlib
cd ..
```

`[verified:src=README.md:188-192]`

> **Do not confuse `formal-conjectures` with `NexusLean/`.** `NexusLean/` is NexusVerifier's own
> Lean project — but it's the InfoTree extraction toolkit (used by the MathlibIngestor), not the
> verification target. Building it is not required for `ingest-parts` or `bench`.
> `[verified:src=NexusLean/NexusLean/Basic.lean — literally `def hello := "world"`, a placeholder]`
> `[verified:src=NexusLean/NexusLean/InfoTreeWalker.lean — the real content, internal elaborator machinery]`

---

## 3. Configure the environment

Copy the example and fill in your values:

```bash
cp .env.example .env
```

The minimum viable `.env` for verification mode:

```bash
# Path to the formal-conjectures checkout you built in step 2
FORMAL_CONJECTURES_PATH=./formal-conjectures
DATA_PATH=./data

# Neo4j credentials (Community edition: user is always "neo4j")
NEO4J_PASSWORD=<your-local-neo4j-password>
NEXUS_NEO4J_DATABASE=neo4j        # Community edition has only "neo4j"; code defaults to "nexusdb"

# Verification policy (the default; safe for benchmarking)
NEXUS_PARTS_NATIVE_DECIDE=reject
```

> **Discrepancy to know:** the README and `.env.example` describe `NEXUS_PARTS_NATIVE_DECIDE` as
> accepting `reject`/`warn`/`allow`, but the code accepts only `reject` (default) and `flag` —
> anything that isn't `"flag"` falls through to `reject`. Neither `warn` nor `allow` is implemented.
> `[verified:src=NexusAgent/NexusAgent.VerifiedParts/VerifiedPartIngestor.cs:59-62]`
> `[verified:src=NexusAgent/NexusAgent.Core/Configuration/NexusConfig.cs:34 — Neo4jDatabase default is "nexusdb", not "neo4j"]`

For the full configuration surface (the ~19 vars the code actually reads, vs the 8 the README
lists), see [`CONFIGURATION.md`](CONFIGURATION.md).

---

## 4. Stand up Neo4j

The easiest path is Docker Compose (the project ships a `docker-compose.yml`):

```bash
docker compose up -d neo4j
```

`[verified:src=docker-compose.yml]` This brings up a Neo4j 5.x instance with the right port
(7687 Bolt) exposed. The first time any NexusVerifier CLI command runs (except `probe`), it
auto-creates the schema (`:ProofFossil`, `:ProofLandmark`, `:MathProblem`, `:LeanCompileCache`,
`:HyperedgeRecord` constraints + the two vector indexes).
`[verified:src=NexusAgent/NexusAgent.Cli/Program.cs:184-189 — EnsureSchemaAsync runs on every command except probe]`
`[verified:src=docs/neo4j_schema.cypher — the schema definition]`

To inspect the schema manually:

```bash
nexus schema       # prints docs/neo4j_schema.cypher
# or, in the Neo4j browser:
# CALL db.schema.visualization()
```

`[verified:src=NexusAgent/NexusAgent.Cli/Program.cs:726-732]`

---

## 5. Build the NexusAgent image (Docker path, recommended)

```bash
docker build -t nexus-agent:latest .
```

`[verified:src=README.md:198-200]` `[verified:src=Dockerfile]` This produces a ~1.6 GB image with
the .NET 10 runtime and NexusAgent binaries baked in. (The Lean + Mathlib toolchain is *not* in
this image — you mount `formal-conjectures` as a volume at runtime.)

### Or build natively (.NET 10)

Skip Docker and build the C# solution directly:

```bash
cd NexusAgent
dotnet build NexusAgent.sln          # 0 warnings, 0 errors expected
```

`[verified:src=README.md:335-339]`

---

## 6. Run a first dry-run verification

The canonical "does my environment work" check is `ingest-parts --dry-run` against the sample
`parts.json` (10 `VerifiedPart` records, real Lean proof terms from the FC corpus).

**Via Docker:**

```bash
docker run --rm \
  -v /path/to/formal-conjectures:/formal-conjectures:ro \
  -v "$(pwd)":/workspace:ro \
  -e NEXUS_NEO4J_URI=bolt://host.docker.internal:7687 \
  -e NEXUS_NEO4J_PASSWORD=$NEO4J_PASSWORD \
  -e NEXUS_NEO4J_DATABASE=neo4j \
  -e NEXUS_LEAN_PROJECT=/formal-conjectures \
  -e NEXUS_PARTS_NATIVE_DECIDE=reject \
  nexus-agent:latest \
  ingest-parts --from-json /workspace/parts.json --dry-run
```

**Native:**

```bash
cd NexusAgent
dotnet run --project NexusAgent.Cli -- \
  ingest-parts --from-json ../parts.json --dry-run
```

`[verified:src=README.md:202-224]` `[verified:src=parts.json — real 10-entry VerifiedPart array, mostly Scope:Weaker]`

Expected output (re-verified 2026-07-26 against the fixed pipeline):
`[verified:src=README.md:218-224]`

```
[ingest-parts] 10 parts from parts.json  sinks=[fossil]  (DRY RUN — no writes)
  FAIL  erdos_1148.variants.lower_bound  — native axioms (policy=reject): [...]
  PASS  [Weaker] erdos_647.variants.twenty_four  axioms=[propext, ...]
  ...
[ingest-parts] Done: 7 passed, 3 rejected, 0 excluded (holdout).
```

> **What this proves:** your Lean toolchain works (`AxiomChecker` ran `lake env lean` and parsed
> `#print axioms`), your Neo4j is reachable (the schema was ensured), and the verification pipeline
> produces the expected 7-pass-3-reject gate sequence on the canonical sample.
> `[verified:src=NexusAgent/NexusAgent.VerifiedParts/AxiomChecker.cs:38-66]`

---

## 7. Run the test suite

```bash
cd NexusAgent
dotnet test NexusAgent.Tests/NexusAgent.Tests.csproj \
  --filter "Category!=Integration" -v q
```

`[verified:src=README.md:341-345]`

**Test count reality check:** the README says "122 tests." The actual count of `[Fact]`/`[Theory]`
attributes is **104 in `NexusAgent.Tests` + 21 in `NexusAgent.ToposExperiment.Tests` = 125**. The
"122" figure likely excludes either the 3 Neo4j-integration tests in `ProofFossilizerTests`
(which require a live Neo4j instance and aren't tagged `Category=Integration` yet — they'll fail
without one `[verified:src=README.md:341-343]`) or the entire ToposExperiment test project.

`[verified:src=NexusAgent/NexusAgent.Tests/ — 104 attribute-marked tests across 13 files]`
`[verified:src=NexusAgent/NexusAgent.ToposExperiment.Tests/ — 21 attribute-marked tests across 5 files (Neo4jParityTests.cs has 0)]`

---

## 8. (Optional) Try proof search

Verification mode (step 6) is the lower-stakes path. Active proof search (`solve` / `bench`)
requires at least one LLM provider configured. Add to `.env`:

```bash
# Pick one or more — leave blank to disable
DEEPSEEK_API_KEY=...               # DeepSeek R1 (the cheapest tier that's useful for proofs)
GOOGLE_API_KEY=...                 # Gemini (used as the hallucination gate juror + Tier 3)
DASHSCOPE_API_KEY=...              # Alibaba Qwen cloud
NEXUS_OLLAMA_URL=http://localhost:11434   # Local Ollama (no key needed)
```

`[verified:src=.env.example:26-45]`

Then run a single-problem solve:

```bash
nexus solve path/to/problem.lean --id erdos_228 --domain Erdos --statement "..."
```

`[verified:src=NexusAgent/NexusAgent.Cli/Program.cs:573, 581-583]` See
[`CLI_REFERENCE.md`](CLI_REFERENCE.md) for the full `solve`/`bench` flag surface (planner weights,
fossil thresholds, parallelism, budget cap, etc.).

> **Honest expectation:** against the actual open-problems corpus (`FC100OpenSet1`), the
> proof-search agent solves **0 of 23** genuine open conjectures. This is the correct, expected
> outcome, not a configuration problem with your environment. Don't tune planner weights expecting
> to do better on open problems — the limitation is mathematical, not parametric.
> `[verified:src=README.md:62-72]` `[verified:docs=docs/FC100_GAP_SET_METHODOLOGY.md]`

---

## 9. Where to go next

- **Look up a command** → [`CLI_REFERENCE.md`](CLI_REFERENCE.md). All 8 commands with flags.
- **Look up a config knob** → [`CONFIGURATION.md`](CONFIGURATION.md). The real env-var surface.
- **Understand the system** → [`ARCHITECTURE.md`](ARCHITECTURE.md). The unified internal picture.
- **The Topos integration** → [`TOPOS_INTEGRATION_REPORT.md`](TOPOS_INTEGRATION_REPORT.md).
- **The benchmark correction** → [`FC100_GAP_SET_METHODOLOGY.md`](FC100_GAP_SET_METHODOLOGY.md).
  Read this before citing any benchmark number from the older runs.
- **The Lean-native hypergraph** (orthogonal to Topos, possibly Phase 1 of a different line) →
  [`hypergraph-engine.md`](hypergraph-engine.md).
