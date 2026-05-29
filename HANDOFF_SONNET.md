# NexusAgent — Sonnet Handoff

**Date**: 2026-05-28  
**From**: Claude Sonnet 4.6 (this session)  
**To**: Claude Sonnet 4.6 (next session)  
**Last commit**: `30e105a`  
**Note**: User will paste Opus code tomorrow — apply it before Phase 9 restart

---

## 1. What Was Done This Session

### §11 / §12 — Depth-2 Chainer Validation ✅
- §11 proved depth-1=GAP, depth-2=PROVED, neg=GAP (chainer works)
- §12 confirmed ceiling at depth-2 (engine can't reach depth-3)
- Both gated behind `NEXUS_HG_DEPTH2_TEST=1` / `NEXUS_HG_LEANDOJO_TEST=1` env vars
- Early-return guards added to §8 for both env vars

### Holdout Verdict in Neo4j ✅
- Replaced unreliable `isSelfCitation` heuristic with authoritative `survivesHoldout` field
- `HgScanRun`: added `IsHoldoutRun = false` (default, backward compat)
- `HgParallelSolver.ScanAsync`: new `bool holdout` param → passes `NEXUS_HG_HOLDOUT=1` to Lean
- `Neo4jClient`: `survivesHoldout = isHoldoutRun && g.Proved` on HgGoalResult nodes
- `Program.cs`: `--holdout` flag on `scan-hg` command
- **70 tests pass** (4 new holdout tests in `ScanRunStoreTests.cs`)
- Schema now has **17 indexes** (added 2 new holdout indexes)
- Holdout scan `1a504aa0`: 4 goals with `survivesHoldout=true`:
  - `OeisA67720.a_1`, `OeisA67720.a_6` (non-equality goals, non-trivial)
  - `PellNumbers.pellNumber_two` (decorative — lhs ≡ rhs by rfl)
  - `WrittenOnTheWallII.Test.C6_size` (decorative — lhs ≡ rhs by rfl)

### §13 LeanDojo Benchmark ✅
- `scripts/leandojo_benchmark_gen.py`: samples 100 from LeanDojo benchmark4 `random/test.json`
- `data/leandojo_test100.json`: 100 entries, p10/p50/p90 state length: 45/129/182 chars
- `leanDojo100Decls` in ErdosHypergraph.lean uses **string `.toName`** (not `\`\`` antiquotation)
  — this is critical: `\`\`` fails at compile time for names not in current Mathlib version
- **Result: PROVED=0/94, NOT_IN_ENV=6** — honest generalization baseline established
- Holdout graph has only 32 edges → zero FC100-specific lemmas → 0 proofs is expected

---

## 2. Current State

### ErdosHypergraph.lean (§ layout)
```
formal-conjectures/_nexus_tmp/ErdosHypergraph.lean  (~1,400 lines)
  §1-§7   : Hypergraph types, fromHGE, extractEdge, searchProofMeta
  §8      : FC100 scan (gated: skip on NEXUS_HG_DEPTH2_TEST=1 or NEXUS_HG_LEANDOJO_TEST=1)
  §9      : LOO (leave-one-out) validation
  §10     : Similarity search for holdout targets
  §11     : Depth-2 chainer validation (run: NEXUS_HG_DEPTH2_TEST=1)
  §12     : Depth-3 ceiling check
  §13     : LeanDojo benchmark (run: NEXUS_HG_LEANDOJO_TEST=1)
```

### Neo4j (bolt://localhost:7687, nexusdb, user: neo4j, pw: REDACTED)
- `HgScanRun ac5e0a97`: standard scan — 39 proved, 61 gap
- `HgScanRun 1a504aa0`: holdout scan — 4 `survivesHoldout=true`, 96 gap
- Vector index `goalshape_state_vec_idx` ONLINE (249K nodes with stateVector)
- 17 total schema indexes

### Tests
```bash
cd NexusAgent && dotnet test NexusAgent.Tests --no-build -c Release
# 70 tests, all pass
```

---

## 3. Tomorrow's Plan

### Step 1: Apply Opus Code (user will paste)
User has Opus-authored code for improvements listed in HANDOFF_OPUS.md §6-§7:
- Lean error messages in LLM prompt (`PromptBuilder.BuildProverRequest`)
- Downrank unverified cross-run fossils in `ProofFossilizer.FindCandidatesAsync`
- `CompilationVerified` property on `ProofFossil`
- Possible: `BestFirstGraphPlanner` spec from §10 of HANDOFF_OPUS.md

**Apply these first**, rebuild, run tests.

### Step 2: Restart Phase 9 Benchmark
```bash
cd /Users/nassertowfigh/Projects/DeepMind-Nexus-Challenge
NEXUS_LEAN_PROJECT=/Users/nassertowfigh/Projects/DeepMind-Nexus-Challenge/formal-conjectures \
  dotnet run --project NexusAgent/NexusAgent.Cli -c Release -- \
  bench data/erdos_phase9_ams5 \
  --source Erdos \
  --max-episodes 5 \
  --max-turns 12 \
  --budget 198.00 \
  >> NexusAgent/NexusAgent.Cli/bin/Release/net10.0/logs/nexus-phase9-$(date +%Y%m%d).log 2>&1 &
```
- 67 problems remaining (8 already done in prior run)
- Fixed bugs: direct-substitution loop (triedFossils HashSet), CrossRun Cypher NULL
- Known issue: no skip-if-already-solved logic → re-runs solved ones (fast via fossil)

### Step 3 (optional): Improve §13 coverage
- Current: 0/94. To improve, seed graph needs more generic Mathlib lemmas
- Run a **non-holdout** scan first to populate `hg_cache.hge`, then re-run §13 with that
- Or: extend chainer to depth-3

---

## 4. Key Commands

```bash
# Build
cd NexusAgent && dotnet build NexusAgent.sln -c Release --no-restore

# Tests
cd NexusAgent && dotnet test NexusAgent.Tests -c Release

# FC100 scan (standard)
cd /Users/nassertowfigh/Projects/DeepMind-Nexus-Challenge
NEXUS_LEAN_PROJECT=formal-conjectures \
  dotnet run --project NexusAgent/NexusAgent.Cli -c Release -- scan-hg --shards 8 --timeout-minutes 10

# FC100 scan (holdout — no FC100 lemmas in seed)
NEXUS_LEAN_PROJECT=formal-conjectures \
  dotnet run --project NexusAgent/NexusAgent.Cli -c Release -- scan-hg --shards 8 --timeout-minutes 10 --holdout

# §13 LeanDojo benchmark
cd formal-conjectures && NEXUS_HG_LEANDOJO_TEST=1 lake env lean _nexus_tmp/ErdosHypergraph.lean 2>&1 | grep -E "§13|PROVED|NOT IN|══"

# §11 depth-2 validation
cd formal-conjectures && NEXUS_HG_DEPTH2_TEST=1 lake env lean _nexus_tmp/ErdosHypergraph.lean 2>&1 | grep -E "§11|PROVED|GAP|depth"
```

---

## 5. Architecture Notes

- `fromHGE` is `Hypergraph.fromHGE : String → IO (Option Hypergraph)`
- In `CommandElabM`, call it as: `match ← Hypergraph.fromHGE path with | some g => ... | none => ...`
- Do NOT use `liftCoreM <| (fromHGE ...).toEIO ...` — that idiom doesn't work here
- `leanDojo100Decls` must use `"Name".toName` not `\`\`Name` — the latter fails compile if name doesn't exist in current Mathlib
- Neo4j DDL must use auto-commit sessions (not explicit transactions) for schema commands

---

## 6. File Paths

| What | Path |
|------|------|
| C# solution | `NexusAgent/NexusAgent.sln` |
| CLI entry | `NexusAgent/NexusAgent.Cli/Program.cs` |
| Neo4j client | `NexusAgent/NexusAgent.Core/Memory/Neo4jClient.cs` |
| Holdout solver | `NexusAgent/NexusAgent.Core/Memory/HgParallelSolver.cs` |
| Scan run record | `NexusAgent/NexusAgent.Core/Memory/HgScanRun.cs` |
| Scan run tests | `NexusAgent/NexusAgent.Tests/Memory/ScanRunStoreTests.cs` |
| Engine (Lean) | `formal-conjectures/_nexus_tmp/ErdosHypergraph.lean` |
| Phase 9 data | `data/erdos_phase9_ams5/` (75 problems) |
| LeanDojo sample | `data/leandojo_test100.json` |
| Benchmark gen | `scripts/leandojo_benchmark_gen.py` |
| Neo4j schema | `neo4j_schema.cypher` (17 statements) |
| Opus handoff | `HANDOFF_OPUS.md` |
