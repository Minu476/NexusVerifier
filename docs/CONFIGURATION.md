# Configuration

**Date:** 2026-07-27 · **Author:** GLM-5.2 (ZCode) · **Audience:** anyone configuring NexusVerifier.
The complete environment-variable surface, source-verified against
`NexusAgent/NexusAgent.Core/Configuration/NexusConfig.cs` (the `ApplyEnvironmentOverrides()`
method) and `.env.example`. Every var the code actually reads is listed; README omissions and
discrepancies are called out explicitly.

> The README lists 8 environment variables. The code actually reads **~19**, plus a few that are
> read by the Ollama daemon or Docker Compose rather than NexusVerifier itself. This doc is the
> source-of-truth surface. `[verified:src=NexusAgent/NexusAgent.Core/Configuration/NexusConfig.cs:72-108]`

---

## How configuration is loaded

Three sources, in priority order (highest first):

1. **Environment variables** (always win). `[verified:src=NexusAgent/NexusAgent.Core/Configuration/NexusConfig.cs:72 — ApplyEnvironmentOverrides]`
2. **`appsettings.json`** under the `"Nexus"` section. `[verified:src=NexusAgent/NexusAgent.Cli/Program.cs:67 — builder.Configuration.GetSection("Nexus").Bind(cfg)]`
3. **Hardcoded defaults** in `NexusConfig`.

`.env` is loaded by Docker Compose (or your shell, if you source it manually) — it's not read by
the C# code directly. `[verified:src=docker-compose.yml]`

---

## Paths

| Variable | Default | Purpose | Source |
|---|---|---|---|
| `FORMAL_CONJECTURES_PATH` | `./formal-conjectures` | Path to the built `formal-conjectures` checkout. Used by Docker Compose to mount it into the container; the C# code reads `NEXUS_LEAN_PROJECT` instead (see below). | `.env.example:7` |
| `DATA_PATH` | `./data` | Directory containing `parts.json`, `tactics_vocab.json`, etc. Docker Compose-level. | `.env.example:10` |
| `NEXUS_LEAN_PROJECT` | (required) | The path NexusVerifier's `LeanOracle` shells out to (`lake env lean`). **This is the var the code reads**, not `FORMAL_CONJECTURES_PATH`. | `NexusConfig.cs:96` |
| `NEXUS_TACTIC_VOCAB` | `data/tactics_vocab.json` | Path to the tactic vocabulary file. | `NexusConfig.cs:103` |

---

## Neo4j

| Variable | Default | Fallback | Purpose | Source |
|---|---|---|---|---|
| `NEXUS_NEO4J_URI` | `bolt://localhost:7687` | `NEO4J_URI` | Bolt URI for the graph backend. | `NexusConfig.cs:90` |
| `NEXUS_NEO4J_USER` | `neo4j` | `NEO4J_USERNAME` | Neo4j user. | `NexusConfig.cs:91` |
| `NEXUS_NEO4J_PASSWORD` | (empty) | `NEO4J_PASSWORD` | Neo4j password. | `NexusConfig.cs:92` |
| `NEXUS_NEO4J_DATABASE` | **`nexusdb`** | — | Database name. | `NexusConfig.cs:93` |

> **Discrepancy 1 — Neo4j database default.** The README and `.env.example` say `neo4j`; **the
> code's default is `nexusdb`**. `[verified:src=NexusAgent/NexusAgent.Core/Configuration/NexusConfig.cs:34, 93]`
> On Neo4j Community Edition (the Docker default), only the `neo4j` database exists — so a user
> who doesn't set `NEXUS_NEO4J_DATABASE` explicitly will see connection errors when the code tries
> to use `nexusdb`. **Always set `NEXUS_NEO4J_DATABASE=neo4j` on Community Edition.**
> `[verified:src=.env.example:16-18 — the comment correctly notes "default 'neo4j' works for Community edition"]`

> **The fallback vars are an FSDE-shared-infrastructure convenience.** The `NEO4J_URI` /
> `NEO4J_USERNAME` / `NEO4J_PASSWORD` fallbacks exist because Nasser's local machine runs a shared
> Neo4j instance used by multiple projects (Topos, FSDE, TradingSystem), and those projects
> resolve the credential from macOS Keychain into the unprefixed env vars. See Topos's
> `docs/GDS_ORACLE_SETUP.md` for the full credential-isolation writeup. You won't need these
> fallbacks unless you're on that same machine.

---

## Verification policy

| Variable | Default | Purpose | Source |
|---|---|---|---|
| `NEXUS_PARTS_NATIVE_DECIDE` | `reject` | Controls the axiom gate's behaviour for `decide +native` / `Lean.ofReduceBool` / `Lean.trustCompiler`. See values below. | `VerifiedPartIngestor.cs:59` |

### Values (and the discrepancy)

| Value (code) | Behaviour | Verified |
|---|---|---|
| `reject` (default) | Parts using native axioms are rejected at the axiom gate. | `VerifiedPartIngestor.cs:60, 94-96` |
| `flag` | Parts are accepted but tagged `:native-flagged` in the `SourceProblems` field. | `VerifiedPartIngestor.cs:60-62` |
| (anything else) | Falls through to `reject`. | `VerifiedPartIngestor.cs:60-62 — the else branch` |

> **Discrepancy 2 — verification policy values.** The README documents three values: `reject`
> (default), `warn`, `allow`. `.env.example` documents two: `reject`, `allow`. **Neither `warn`
> nor `allow` is implemented in code.** The code accepts only `reject` (default) and `flag`;
> anything else falls through to `reject`. A user setting `NEXUS_PARTS_NATIVE_DECIDE=warn`
> (per the README) or `=allow` (per `.env.example`) will silently get `reject` behaviour.
> `[verified:src=README.md:301-306 — the README's table]` `[verified:src=.env.example:20-24 — the env-example's table]`
> `[verified:src=NexusAgent/NexusAgent.VerifiedParts/VerifiedPartIngestor.cs:59-62 — the actual code]`

---

## LLM providers (all optional)

Verification mode (`ingest-parts`) needs none of these. Proof-search mode (`solve`/`bench`) needs
at least one. The `TieredLlmRouter` escalates: Tier1 (cheap) → Tier2 (DeepSeek Flash) → Tier3
(premium cloud), with a hard budget cap and a circuit breaker. See
[`ARCHITECTURE.md`](architecture.md) §3 for the tier ladder.

### DeepSeek (Tier 2 — the proof-search workhorse)

| Variable | Default | Purpose | Source |
|---|---|---|---|
| `DEEPSEEK_API_KEY` | (empty) | API key. Leave blank to disable. | `NexusConfig.cs:75` |
| `NEXUS_DEEPSEEK_BASE_URL` | `https://api.deepseek.com/v1` | Base URL (override for proxies/self-hosting). | `NexusConfig.cs:76` |

### Google Gemini (Tier 3 + hallucination-gate juror)

| Variable | Default | Purpose | Source |
|---|---|---|---|
| `GOOGLE_API_KEY` | (empty) | API key. Gates whether the `GeminiClient` is registered. | `NexusConfig.cs:79`, `Program.cs:104` |
| `NEXUS_GEMINI_BASE_URL` | `https://generativelanguage.googleapis.com/v1beta/openai` | OpenAI-compatible base URL. | `NexusConfig.cs:80` |
| `NEXUS_GEMINI_MODEL` | `gemini-2.5-flash` | Model tag. | `NexusConfig.cs:81` |

> **README discrepancy 3 (minor):** `.env.example` shows the model default as `gemini-2.0-flash`
> and the env var name as `GEMINI_API_KEY`; the code reads `GOOGLE_API_KEY` and defaults the model
> to `gemini-2.5-flash`. `[verified:src=.env.example:30-32]` `[verified:src=NexusAgent/NexusAgent.Core/Configuration/NexusConfig.cs:79-81]`

### Alibaba Qwen / DashScope (Tier 1 cloud option)

| Variable | Default | Purpose | Source |
|---|---|---|---|
| `DASHSCOPE_API_KEY` | (empty) | API key. Gates `QwenCloudClient` registration. | `NexusConfig.cs:84`, `Program.cs:124` |
| `NEXUS_DASHSCOPE_BASE_URL` | `https://dashscope-intl.aliyuncs.com/compatible-mode/v1` | Base URL. | `NexusConfig.cs:85` |
| `NEXUS_QWEN_CLOUD_MODEL` | `qwen3.7-max` | Model tag. | `NexusConfig.cs:86` |

### Ollama (Tier 1 local — no API key needed)

| Variable | Default | Purpose | Source |
|---|---|---|---|
| `NEXUS_OLLAMA_URL` | `http://localhost:11434` | Ollama base URL. Use `http://host.docker.internal:11434` from inside Docker. | `NexusConfig.cs:99` |
| `NEXUS_QWEN_MODEL` | `qwen3.6:35b-a3b` | Local Qwen model tag. | `NexusConfig.cs:100` |
| `OLLAMA_MODELS` | (not set) | Surfaces local Ollama models in the `probe` command's output. Read by the Ollama daemon, not NexusVerifier. | `Program.cs:476` |

---

## Budget guard

| Variable | Default | Purpose | Source |
|---|---|---|---|
| `NEXUS_BUDGET_USD` | `200m` | Maximum spend in USD before the `TieredLlmRouter` halts. `0` = unlimited. | `NexusConfig.cs:105-107` |

The router tracks cumulative spend across all tiers and refuses to dispatch a call that would
exceed the cap. `[verified:src=NexusAgent/NexusAgent.Core/Llm/TieredLlmRouter.cs:132 — BudgetCapUsd=200]`

---

## Scan-hg only

| Variable | Default | Purpose | Source |
|---|---|---|---|
| `NEXUS_HG_HOLDOUT` | (not set) | Passed through to Lean when running `scan-hg --holdout`. | `Program.cs:231` |

---

## Minimum-viable `.env` files

### Verification mode only (no LLM keys needed)

```bash
FORMAL_CONJECTURES_PATH=./formal-conjectures
DATA_PATH=./data
NEO4J_PASSWORD=<your-local-neo4j-password>
NEXUS_NEO4J_DATABASE=neo4j                       # CRITICAL on Neo4j Community Edition
NEXUS_LEAN_PROJECT=$FORMAL_CONJECTURES_PATH
NEXUS_PARTS_NATIVE_DECIDE=reject
```

### Proof-search mode (add LLM keys)

```bash
# ...everything above, plus at least one of:
DEEPSEEK_API_KEY=...                             # Tier 2 — the workhorse
GOOGLE_API_KEY=...                               # Tier 3 + hallucination juror
NEXUS_GEMINI_MODEL=gemini-2.5-flash              # match the key you have

# Optional: cap spend
NEXUS_BUDGET_USD=20.00
```

### Local-only proof search (no cloud)

```bash
# Ollama running locally with a Qwen model pulled
NEXUS_OLLAMA_URL=http://localhost:11434
NEXUS_QWEN_MODEL=qwen3.6:35b-a3b
```

---

## The three discrepancies, summarized

For the shipping-decision record. Each is a real defect where the docs and code disagree; this
doc follows the code.

| # | What | Docs say | Code does | Source |
|---|---|---|---|---|
| 1 | `NEXUS_NEO4J_DATABASE` default | `neo4j` | `nexusdb` | `NexusConfig.cs:34, 93` |
| 2 | `NEXUS_PARTS_NATIVE_DECIDE` values | `reject`/`warn`/`allow` (README), `reject`/`allow` (.env.example) | `reject`/`flag` only; anything else → `reject` | `VerifiedPartIngestor.cs:59-62` |
| 3 | Gemini env var name + model default | `GEMINI_API_KEY`, `gemini-2.0-flash` | `GOOGLE_API_KEY`, `gemini-2.5-flash` | `NexusConfig.cs:79-81` |

**Recommended fixes** (for a code or README session — both are outside the doc-only lane I
worked in for the body docs, but recording them here is documentation):
- README: change the `NEXUS_PARTS_NATIVE_DECIDE` table to `reject` (default) / `flag`, and add
  the "anything else falls through to reject" note.
- `.env.example`: change `allow` to `flag`, and align the Gemini var/model.
- Code or README: pick one source of truth for the `NEXUS_NEO4J_DATABASE` default. The code's
  `nexusdb` is friendlier for isolation across experiments but wrong for Community Edition out of
  the box; the README's `neo4j` is friendlier for first-run but loses isolation. Either is
  defensible — pick deliberately and make docs match.

---

## Cross-references

- **The architecture this configuration drives** → [`architecture.md`](architecture.md).
- **The commands that consume these vars** → [`CLI_REFERENCE.md`](CLI_REFERENCE.md).
- **`.env.example` (the template)** → [`../.env.example`](../.env.example).
