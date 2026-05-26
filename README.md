# DeepMind Nexus Challenge

Rich Learning V2 graph-augmented formal proof search, benchmarked against
Google DeepMind's AlphaProof Nexus (arXiv:2605.22763, May 2026).

## Hypothesis

Rich Learning's DAPSA architecture — fossil vault for sub-goal reuse,
Cartographer for topological proof navigation, ConsonanceChecker for
hallucination filtering — can match AlphaProof Nexus's results on OEIS
conjectures at a fraction of the per-problem cost, using a tiered LLM
strategy: local Qwen3.6 for cheap exploration, DeepSeek V4 API for hard
problems.

## Quick start

See [SPEC.md](SPEC.md) for architecture, component specs, and build plan.
See [ENHANCEMENT_GUIDE.md](ENHANCEMENT_GUIDE.md) for tuning each component
once you have early results.

```bash
# 1. Lean
curl https://raw.githubusercontent.com/leanprover/elan/master/elan-init.sh -sSf | sh
cd NexusLean && lake update && lake build       # builds Mathlib cache (~30 min once)

# 2. Local LLM
ollama pull qwen3.6:35b-a3b                      # ~20 GB; runs in 4–6 GB RAM (3B active params)

# 3. Neo4j
brew install neo4j && neo4j start
# set password via http://localhost:7474, then edit appsettings.json

# 4. DeepSeek API key
# Edit NexusAgent.Cli/appsettings.json → Nexus.DeepSeekApiKey

# 5. Build and run
cd NexusAgent && dotnet build
cd NexusAgent.Cli && dotnet run -- bench ../../NexusLean/Problems/OEIS --source OEIS
```

## Stack

| Layer | Technology |
|---|---|
| Formal proof language | Lean 4 + Mathlib |
| LLM Tier 0 | Fossil vault (free) |
| LLM Tier 1 | Qwen3.6-35B-A3B local via Ollama (free, 3B active params) |
| LLM Tier 2 | DeepSeek V4-Flash API ($0.14/M in, $0.28/M out) |
| LLM Tier 3 | DeepSeek V4-Pro API ($0.435/M in, $0.003625/M cached, $0.87/M out) |
| Graph backend | Neo4j with vector indexes |
| Framework | Rich Learning V2 (DAPSA) |
| Language | C# / .NET 10 |
| Hardware | Mac Mini M4 Pro, 48 GB RAM |

## Benchmark targets

- OEIS conjectures: ≥ 10 solved from 50-problem local subset
- Erdős problems: ≥ 1 (stretch target, publication-grade)
- Fossil hit rate after 20 problems: ≥ 30%
- Total spend on full 50-problem OEIS benchmark: ≤ $150
  (vs. ~$1,600 equivalent at Gemini Pro rates)
