# Transition-dump → ToposTacticStore A/B — pre-registered design

**Written:** 2026-08-14, BEFORE any substrate work (dump pipeline, v4.33 port) — per review
directive: the design doc precedes the two days that build what it measures.
**Status:** pre-registered. Any deviation during execution gets logged in the results writeup
as a deviation, not folded in silently.

---

## 1. Question

Does seeding `ToposTacticStore` with real tactic transitions (dumped from building
`FormalConjecturesForMathlib` via `nexus.dumpInfoTreesPath`) raise the independent-solve
rate above the fossil-only baseline?

**Why this experiment:** the store's three prior runs were 13/31 (baseline, store inert),
10/31 (store, ungated), 12/31 (store, gated). The hypothesis for the regression has always
been "the store is near-empty, so nearest-neighbor returns false positives" — the dump is
the first test where that excuse is unavailable.

## 2. Arms

| | Control | Treatment |
|---|---|---|
| Store seeding | historical fossil vault only (as every baseline run) | same vault **plus** transition-dump records |
| Neo4j | `nexus-neo4j` container, bolt://localhost:27687, db `neo4j` — untouched | **separate container** on 27690, cold-restored copy of the control db, dump records ingested only there |
| Everything else | identical: parallel 12, max-episodes 3, max-turns 10, --graph-first, same router/tiers/budget cap | identical |

## 3. Corpus

- **Primary:** `data/swarm_solvable` — 31 problems. Baseline 13/31 (42%) independent
  (`TOPOS_RERUN_VS_BASELINE_2026-08-03.md`).
- **Secondary (exploratory, pre-registered as underpowered):** primary + the 17-problem
  fully-prevented corpus (48 total, combined baseline 14/48). Near-floor subcorpus; dilutes,
  does not strengthen, the primary.

## 4. Headline metric & analysis (pre-committed)

Per-problem paired binary outcome: **independent solve (yes/no)** after citation audit
(solve-time CitationDetector + `#print axioms` — unconditional, unchanged by Phase 2).

Exact McNemar (two-sided, α = 0.05) on discordant pairs b (control-only solves) and
c (treatment-only solves).

### 4.1 Minimum detectable effect — the number that shapes everything

At n = 31 with baseline 13/31:

| Scenario (b = treatment gains, c = treatment losses) | Treatment solves | exact p | Significant? |
|---|---|---|---|
| b=6, c=0 | 19/31 | 0.031 | **yes** |
| b=5, c=0 | 18/31 | 0.063 | no |
| b=9, c=1 | 21/31 | 0.021 | **yes** |
| b=10, c=2 | 22/31 | 0.039 | **yes** |
| b=7, c=2 | 20/31 | 0.180 | no |

**To clear significance, treatment needs ≥ +6 net conversions with zero regressions
(19/31), or 21–22/31 if it loses 1–2 baseline solves.** That is a 45–70% relative jump —
far larger than any effect a retrieval layer has produced here (the store's observed
effects to date are −3 to −1 problems).

**Pre-registered conclusion from this arithmetic:** n=31 **cannot** detect the effect size
we actually expect. The primary run is therefore **directional evidence + a pipeline
validity check** (does dump-seeded retrieval fire and not hurt?), not a significance test.
We do NOT expand the primary tier because the only honest expansions are at floor
(openset1: 0/31; prevented: 1/17) and would manufacture apparent power out of noise.
If the directional result is b ≥ 4 with c ≤ 1, the follow-up is a pre-registered
replication on a purpose-built ~60-problem tier, not a re-run of this one.

## 5. Store isolation (controlled, not acknowledged)

1. Control runs against 27687 first, untouched.
2. Treatment container (27690) is created from a **cold `neo4j-admin database dump`** of
   the 27687 `neo4j` db taken AFTER the control run finishes (so both see the same fossil
   history including control's fossils — paired in time).
3. Dump records are ingested into the treatment db only, tagged `source: 'MATHLIB_DUMP'`.
4. **Verification query, run on BOTH dbs, output pasted into the writeup:**
   `MATCH (n) WHERE n.source = 'MATHLIB_DUMP' RETURN count(n)` → control MUST be 0,
   treatment MUST be > 0. A nonzero control count invalidates the run.
5. Post-run, both dbs are counted for new fossils per arm (carryover direction recorded).

## 6. Source tags & the silent-skip hazard

- Fresh tags: `TDABC1` (control primary), `TDABT1` (treatment primary); `TDABC2`/`TDABT2`
  for the secondary if run. Never reused — `Program.cs`'s already-solved check silently
  skips problems tagged Solved in Neo4j (nearly corrupted round 3; Phase 2 makes the skip
  loud). Before launch: assert each tag returns zero prior records.

## 7. Operational safety

- **Do not launch TradingSystem's Visualizer or CoreConsoleApp while a run is active** —
  the `HandleLockedDllFiles()` stub SIGKILLed every dotnet process on this machine during
  the week of 2026-08-10. Confirm the fix is deployed before the overnight window; if it
  cannot be confirmed, run arms in daytime with periodic checks instead.
- Slow-request heartbeat (45 s first warning, then 60 s cadence) is the liveness signal.
  A quiet log is NOT evidence of death — the 2026-08-04 misdiagnosis.
- **Abort criterion:** at 08:00, `pgrep -f NexusAgent` (not `ps aux | grep`). Heartbeat
  shows in-flight requests → let it run to a hard cap of 10:00, then capture-and-kill.
  Quiet > 30 min → capture logs, kill, record partials as partials.
- Budget cap $200 per arm (router-configured); actual spend logged in the writeup.

## 8. Pre-committed interpretations

- **c > b** (net harm): dump seeding actively hurts at this scale — gate thresholds
  recalibrated or the store retired. Writeup either way.
- **b ≤ 2:** null; the "near-empty store" hypothesis is dead and the sparse-store lesson
  gets updated with "even a real store doesn't help at 31 problems."
- **b ≥ 4, c ≤ 1:** directional positive; warrants the powered replication tier (§4.1).
- Any solve in either arm gets the full audit trail (citation verdict, axioms, tier trace)
  reproduced verbatim in the writeup.
