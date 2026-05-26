# Architecture: Rich Learning Nexus Agent

## How the components relate

```
┌─────────────────────────────────────────────────────────────────────────┐
│                         NexusOrchestrator                               │
│  Manages episode lifecycle for one problem. No parallelism intentional  │
│  (graph is the shared state; serial episodes + graph restarts are        │
│   cleaner than parallel conflicting graph writes)                        │
└──────────────────────────────┬──────────────────────────────────────────┘
                               │ drives episodes
                               ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                       NexusProverSubagent                               │
│                                                                         │
│  1. ProofStateEncoder  → encode current sketch into float[64]           │
│  2. DapsaEngine        → decide: fossil hit or LLM call                │
│       └─ FossilVault query (Neo4j vector similarity)                    │
│       └─ HallucinationGate scan (sorry lemma filtering)                 │
│       └─ DeepSeek R1  (only if no fossil hit above threshold)           │
│  3. Apply tactic block to sketch                                        │
│  4. LeanOracle.Compile → binary result                                  │
│  5. ProofFossilizer    → persist any newly-proved subgoals              │
│  6. ProofCartographer  → record transition, detect dead ends            │
└─────────────────────────────────────────────────────────────────────────┘
                               │ reads/writes
                               ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                            Neo4j                                        │
│                                                                         │
│  :ProofFossil   (vector index, 64-dim)  ← proven sub-goals persist     │
│  :ProofLandmark (vector index, 64-dim)  ← visited states topology      │
│  :MathProblem                           ← problem registry             │
│  :LeanCompileCache                      ← sketch hash → compile result │
└─────────────────────────────────────────────────────────────────────────┘

                        ↕ subprocess
┌──────────────┐
│  LeanOracle  │   shells out to `lean` / `lake build`
│              │   parses stdout/stderr for goals, errors, sorry count
└──────────────┘
```

## What this improves over AlphaProof Nexus

| DeepMind failure mode | Rich Learning mitigation |
|---|---|
| Sorry offloading into helper lemmas | CartographerPlanner marks dead-end regions; prompt injection redirects |
| Hallucinated "established results" | HallucinationGate: sorry lemmas checked against fossil vault + Mathlib index |
| No cross-problem knowledge | FossilVault persists to Neo4j — available to every future problem |
| Elo / probabilistic fitness mismatch | Deterministic topological potential score (no rating agents needed) |
| $100–300/problem in cloud LLM cost | Local DeepSeek R1 — $0/problem |

## What this does NOT improve

- Raw LLM mathematical reasoning capability: DeepSeek R1 < Gemini 3.1 Pro. The graph
  compensates for search efficiency, not for mathematical imagination at the ceiling.
- Lean formalization skill: still requires the user to provide a valid Lean sketch with
  `sorry` placeholders. The agent fills in the proofs, it doesn't write the spec.
- Parallelism at cloud scale: not the goal. Single-machine, graph-guided serial search.
