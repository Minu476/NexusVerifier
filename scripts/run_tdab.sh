#!/bin/bash
# Pre-registered transition-dump A/B (data/results/TRANSITION_DUMP_AB_DESIGN_2026-08-14.md).
# Stages are individually resumable: run_tdab.sh <stage>  (control | isolate | treatment)
set -euo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO"

PW="$(docker inspect nexus-neo4j --format '{{range .Config.Env}}{{println .}}{{end}}' | grep '^NEO4J_AUTH=' | cut -d/ -f2-)"
DEEPSEEK="$(grep '^DEEPSEEK_API_KEY=' ../FSDE/.env | head -1 | cut -d= -f2-)"
DASHSCOPE="$(grep '^DASHSCOPE_API_KEY=' .env | head -1 | cut -d= -f2-)"
export NEXUS_NEO4J_USER=neo4j NEXUS_NEO4J_PASSWORD="$PW" NEXUS_NEO4J_DATABASE=neo4j
export NEXUS_LEAN_PROJECT="$REPO/formal-conjectures"
export DEEPSEEK_API_KEY="$DEEPSEEK" DASHSCOPE_API_KEY="$DASHSCOPE" NEXUS_BUDGET_USD=200
CLI="$REPO/NexusAgent/NexusAgent.Cli/bin/Release/net10.0/nexus.dll"
BENCH_ARGS=(bench data/swarm_solvable --parallel 12 --max-episodes 3 --max-turns 10 --graph-first)

stage="${1:?usage: run_tdab.sh control|isolate|treatment}"

case "$stage" in
control)
  export NEXUS_NEO4J_URI=bolt://localhost:27687
  dotnet "$CLI" "${BENCH_ARGS[@]}" --source TDABC1
  ;;
isolate)
  # Cold copy of the control-run state into a fresh treatment container on 27690.
  docker rm -f nexus-neo4j-ab 2>/dev/null || true
  docker volume rm tdab-data 2>/dev/null || true
  docker volume create tdab-data
  docker stop nexus-neo4j
  # --entrypoint bash: the neo4j entrypoint force-drops to uid 7474 even under --user root
  docker run --rm --user root --entrypoint bash \
    -v f3c67c81cbe80a37cbacb1ce3d5ca39abaf7699da7750e13a1955c9f99eb8405:/src \
    -v tdab-data:/dst neo4j:5-community \
    -c 'cp -a /src/. /dst/ && chown -R 7474:7474 /dst' || { docker start nexus-neo4j; exit 1; }
  docker start nexus-neo4j
  sleep 20
  docker run -d --name nexus-neo4j-ab -p 27690:7687 -v tdab-data:/data \
    -e NEO4J_AUTH="neo4j/$PW" neo4j:5-community
  sleep 40
  docker exec nexus-neo4j-ab cypher-shell -a bolt://localhost:7687 -u neo4j -p "$PW" "RETURN 1" >/dev/null
  # Ingest dump-derived fossils into the TREATMENT db only.
  export NEXUS_NEO4J_URI=bolt://localhost:27690
  dotnet "$CLI" seed-dump --input /tmp/fcm-transitions.jsonl
  echo "--- verification query (control | treatment must be 0 | >0):"
  docker exec nexus-neo4j cypher-shell -u neo4j -p "$PW" \
    "MATCH (f:ProofFossil) WHERE f.runId='MATHLIB_DUMP' RETURN count(f) AS controlDumpCount"
  docker exec nexus-neo4j-ab cypher-shell -a bolt://localhost:7687 -u neo4j -p "$PW" \
    "MATCH (f:ProofFossil) WHERE f.runId='MATHLIB_DUMP' RETURN count(f) AS treatmentDumpCount"
  ;;
treatment)
  export NEXUS_NEO4J_URI=bolt://localhost:27690
  dotnet "$CLI" "${BENCH_ARGS[@]}" --source TDABT1
  ;;
esac
