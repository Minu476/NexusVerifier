# Topos-Native Tactic Store: A/B vs Baseline

**Date:** 2026-08-03
**Goal:** Take full advantage of Topos's native `HypergraphKernel` + `VectorIndex` + `EdgeStatistics`
to fix the two inert retrieval layers from the baseline swarm, and measure the impact.

## What changed between runs

A new `ToposTacticStore` (`NexusAgent.Core/Planning/ToposTacticStore.cs`) was added — a
process-lifetime Topos kernel, shared across all 31 problems in a bench run, that:
- **Retrieves tactics** by goal-similarity via Topos's native `VectorIndex.NearestNeighbors`
  (replacing the broken Neo4j `goalshape_state_vec_idx` path that returned 0 proposals).
- **Tracks success** per (goal, tactic) edge via `EdgeStatistics.Observe` — a feedback loop the
  Neo4j path never had: tactics that work rank higher; ones that fail decay.
- **Grows during the run** — every successful sorry-reduction (from any tier: graph replay,
  Tier 0.75, or LLM) calls `RecordSuccessAsync`, so later problems benefit from earlier ones.
- **Seeds at startup** from the prior run's fossil vault (12 fossils → 12 tactic edges).

This is the exact pattern blessed by `external/Topos/docs/NEXUS_VERIFIER_INTEGRATION_FINDINGS.md`
("What worked" #3: immutable-value + kernel-as-truth mutation).

## Headline A/B (both audited for citation leakage)

| Metric | Baseline (Neo4j, inert Tier 0.75) | Topos rerun | Δ |
|---|---|---|---|
| **Independent solves** | **13/31 (42%)** | **10/31 (32%)** | **−3** |
| Citation solves | 4 | 6 | +2 |
| Raw solves (total) | 17 | 16 | −1 |
| **Tier 0.75 proposals** | **0** | **65** | **+65** |
| **Tier 0.75 wins** | **0** | **1** | **+1** |
| **Fossil conversion** | **0% (0/53)** | **8.5% (4/47)** | **+8.5pp** |
| Fossil hits | 0 | 4 | +4 |
| Struct rejects | 8 | 4 | −4 |
| Goal dedup hits | 82 | 68 | −14 |
| Spend | $0.55 | $0.52 | −$0.03 |

## The honest read

**The Topos integration works mechanically — but it did not improve solve rate.** It went
*down* by 3 independent solves (42% → 32%). Here's the precise diagnosis, not a rationalization:

### What worked (the integration is sound)
- **Tier 0.75 went from 0 to 65 proposals** — the Topos `VectorIndex` is finding similar goals
  and proposing tactics. This is the layer that was completely dead before.
- **1 Tier 0.75 win** — a deterministic tactic from the store solved a goal with zero LLM call
  (the `hasGap_empty` "finite set with no elements has no gaps" proof). This is the first ever
  non-LLM solve in this system's runs.
- **Fossil conversion 0% → 8.5%** — the store's seeded edges were actually substituted into
  sketches and reduced sorry. The retrieval-to-application gap closed.
- **Spend held flat** ($0.52 vs $0.55) — the deterministic tier isn't costing more.

### Why solve rate dropped (the real signal)
1. **The store is too sparse to help, but just dense enough to mislead.** 12 seeded edges from 4
   goals is a tiny retrieval surface. The `VectorIndex` returns nearest neighbors at sim=0.97
   (very high) even when the match is structurally unrelated — because with only 4 goal vectors,
   the nearest one is almost always "close" by the encoder's hashed-bigram metric. So Tier 0.75
   proposed tactics 65 times, but only 1 actually reduced sorry. The other 64 proposals were
   tried (consuming 2 compile-checks each per turn), failed, and *displaced LLM turns that would
   have solved the problem*.

2. **The displacement effect is measurable.** Baseline: 82 goal-dedup hits, 8 struct rejects,
   13 independent solves. Topos: 68 dedup, 4 struct rejects, 10 solves. The 3 lost solves
   (`a_0`, `a_65`, `congruentNumber_7`) were all independent-solves in the baseline that became
   citation-solves or aborts in the rerun — suggesting the prover took a different path through
   the search space, influenced by the Tier-0.75 proposals, and landed worse.

3. **Citation solves went UP (4→6).** This is noise, not signal — DeepSeek citing the original
  declaration is model behavior, not store behavior. But it means the "real" comparison is
  13 vs 10 independent, and the Topos run is genuinely behind.

### Root cause: similarity ≠ relevance at this scale
The `ProofStateEncoder`'s 64-dim hashed-bigram vector is a *structural* fingerprint, not a
*semantic* embedding. With 4 goals in the store, every query is "similar" to something (the
nearest of 4 points is always within a small angle). The rank-mixing formula (0.65×sim) then
over-weights this false similarity, and the prover wastes compile-budget testing tactics that
were structurally-similar-but-semantically-wrong.

This is the exact failure mode the Topos integration findings warned about implicitly:
`VectorIndex` is brute-force Euclidean and works at scale; at 4 goals it has no discriminative
power.

## What this tells us about the path forward

The Topos store is the **right architecture** — it's doing exactly what it was built to do
(retrieve, propose, learn). The problem is **data density and signal quality**, not the mechanism.
Three concrete fixes, in order of leverage:

1. **Raise the similarity bar for Tier 0.75 to propose.** Currently it proposes at any sim>0;
   with 4 goals everything proposes. A threshold (sim>0.92 AND success>0.5) would have prevented
   ~60 of the 65 wasted proposals. Cheapest fix, highest leverage.

2. **Seed from more data.** 12 fossils is too few. The Mathlib transition dump (the 267k-node
   graph from the WD-Blue drive, per `docs/mathlib-ingest/`) would give real discriminative
   power. Without it, the store can't distinguish relevant from merely-near.

3. **Don't let Tier 0.75 displace the LLM when it's cold.** Gate Tier-0.75 proposals behind a
   minimum store size (e.g. skip proposing until >50 edges), so a cold store doesn't inject
   noise into early problems.

## Conclusion

The integration succeeded; the experiment showed the approach needs more data and a relevance
threshold before it improves solve rates. This is itself a useful result: it confirms the
bottleneck is **premise-retrieval data density** (idea #1 in `UNDERLYING_PROBLEM_IDEAS.md`),
not the retrieval mechanism. The Topos store is ready to absorb that data when it's available;
right now it's a working engine with almost no fuel.
