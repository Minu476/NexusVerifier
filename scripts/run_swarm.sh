#!/usr/bin/env bash
# Aggressive DeepSeek v4-Flash proof-reconstruction swarm.
#
# Runs `nexus bench` over the pre-validated stub corpus with max parallelism,
# then runs the citation audit so the reported solve count separates
# independent proofs from answer-key citations.
#
# Prereqs (all verified by setup):
#   - DEEPSEEK_API_KEY exported in the environment
#   - elan/lake on PATH (~/.elan/bin)
#   - NEXUS_LEAN_PROJECT points at the built formal-conjectures project
#   - nexus-neo4j container running on 27687
set -euo pipefail

REPO="/Users/nassertowfigh/Projects/NexusVerifier"
DLL="$REPO/NexusAgent/NexusAgent.Cli/bin/Release/net10.0/nexus.dll"
RESULTS_DIR="$REPO/data/results"
TS=$(date +%Y-%m-%d_%H-%M-%S)

# Target set: "solvable" (FC100SolvedSet1 proven, tractable) or
#             "openset1" (genuine open research problems, expected ~0 solves)
TARGET="${1:-solvable}"
case "$TARGET" in
  solvable)  STUB_DIR="$REPO/data/swarm_solvable";  SOURCE="SwarmSolv" ;;
  openset1)  STUB_DIR="$REPO/data/swarm_openset1"; SOURCE="OpenSet1"  ;;
  *) echo "Usage: $0 [solvable|openset1]"; exit 1 ;;
esac

# ── env ──────────────────────────────────────────────────────────────────────
export NEXUS_LEAN_PROJECT="$REPO/formal-conjectures"
export PATH="$HOME/.elan/bin:$PATH"
export NEXUS_NEO4J_URI="bolt://localhost:27687"
export NEXUS_NEO4J_USER="neo4j"
if [ -z "${NEXUS_NEO4J_PASSWORD:-}" ]; then echo "ERROR: NEXUS_NEO4J_PASSWORD not set. Export it before running." >&2; exit 1; fi
export NEXUS_NEO4J_DATABASE="neo4j"
export NEXUS_BUDGET_USD="${NEXUS_BUDGET_USD:-200}"

if [ -z "${DEEPSEEK_API_KEY:-}" ]; then
  echo "ERROR: DEEPSEEK_API_KEY not set. Export it first: export DEEPSEEK_API_KEY=sk-..." >&2
  exit 1
fi

mkdir -p "$RESULTS_DIR"
echo "════════════════════════════════════════════════════════════════"
echo "  NexusVerifier DeepSeek v4-Flash Swarm"
echo "  Corpus:    $(ls "$STUB_DIR"/*.lean | wc -l | tr -d ' ') stubs"
echo "  Budget:    \$${NEXUS_BUDGET_USD}"
echo "  Lean proj: $NEXUS_LEAN_PROJECT"
echo "  Started:   $TS"
echo "════════════════════════════════════════════════════════════════"

# ── run ──────────────────────────────────────────────────────────────────────
# Aggressive config: high parallelism (12 = core count), multiple episodes,
# graph-first planner enabled, fossil retrieval on, legacy LLM fallback on.
cd "$REPO"
dotnet "$DLL" bench "$STUB_DIR" \
  --source "$SOURCE" \
  --parallel 12 \
  --max-episodes 3 \
  --max-turns 10 \
  --graph-first \
  2>&1 | tee "$RESULTS_DIR/swarm-${TARGET}-$TS.log"

# ── audit ────────────────────────────────────────────────────────────────────
# Find the newest bench JSON and run the citation audit on it.
LATEST_JSON=$(ls -t "$RESULTS_DIR"/../results/bench-*.json 2>/dev/null | head -1)
# The bench command writes to <dir>/../results/ — STUB_DIR/.. = data/, so data/results/
LATEST_JSON=$(ls -t "$REPO/data/results"/bench-*.json 2>/dev/null | head -1)
if [ -n "$LATEST_JSON" ] && [ -f "$LATEST_JSON" ]; then
  echo ""
  echo "════════════════════════════════════════════════════════════════"
  echo "  Citation Audit"
  echo "════════════════════════════════════════════════════════════════"
  python3 "$REPO/scripts/citation_audit.py" "$LATEST_JSON" --stub-dir "$STUB_DIR"
  echo ""
  echo "Results: $LATEST_JSON"
else
  echo "WARNING: no bench-*.json found to audit. Check $REPO/data/results/"
fi
