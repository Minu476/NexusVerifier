# Solving the underlying problem — where to attack next

This is an analysis of *why* the prover gets 0/23 on genuinely-open problems and what
would actually move that number, grounded in reading the codebase end-to-end. It's
deliberately separate from the swarm run (which tests brute-force sampling) — these are
ideas for changing the **approach**, ranked by expected impact and effort.

## The core diagnosis

The prover is a **single-line sketch refiner**: each turn, DeepSeek rewrites the entire
proof sketch to reduce sorry count by ≥1. This has three structural weaknesses that no
amount of aggressive sampling fixes:

1. **No premise retrieval.** The prompt (PromptBuilder.cs) gives the model the current
   sketch and compiler errors, but never says *"here are the Mathlib lemmas relevant to
   this goal."* For a goal like `IsMaximalSidonSetIn {1,2,4} 4`, the model must *guess*
   which of Mathlib's ~150k lemmas to cite. On textbook problems it can; on research
   problems it can't — and the gap is exactly premise knowledge, not reasoning depth.

2. **Whole-sketch rewriting is fragile.** `SubstituteFirstSorry` + "rewrite the whole
   sketch" means one bad token anywhere reverts all progress. The Tier 0.75 graph-tactic
   probe does single-tactic substitution, but only from a small pre-indexed set. There's
   no middle ground: "let the LLM propose *one tactic* for *this specific goal*, then
   deterministically splice it in."

3. **No proof-tree search.** Each episode is a greedy descent on sorry count
   (NexusProverSubagent.RunEpisodeAsync). If the model's first move is a dead end, the
   episode spends remaining turns flailing. BestFirstGraphPlanner exists but operates on
   the offline goal-shape graph, not on live LLM-proposed branches. There's no best-first
   search over LLM-generated tactic candidates with Lean as the verifier.

## Ideas, ranked

### 1. Premise retrieval via Mathlib library search (HIGHEST IMPACT)

**The gap this closes:** the model doesn't know which lemmas exist.

**Concrete plan:**
- For each pending goal, extract its head symbol and type (already available via Lean's
  goal-state pretty-print — `ProofState.PendingGoals`).
- Query Mathlib's declaration index for lemmas whose conclusion matches the goal shape.
  Two cheap options:
  - **moogle / loogle**: existing Lean lemma-search services. HTTP query with the goal
    type, get back candidate lemma names. Add as a new `IPremiseRetriever`.
  - **Local Mathlib ingest**: the repo already has `NexusAgent.MathlibIngestor` and
    `docs/mathlib-ingest/` cypher. The Neo4j goal-vector graph (`ProposeTacticsFromGoalVectorAsync`)
    is the right shape but currently indexes only *tactics seen in FC100*, not Mathlib's
    full lemma space. Extend the ingestor to index Mathlib theorem conclusions as
    retrievable premises.
- Inject the top-K retrieved lemmas into the prompt as `"# Relevant Mathlib lemmas"` —
  stable-prefix, cache-friendly.

**Why this is #1:** AlphaProof's single biggest component is premise selection. Our
prover has none. This is the largest capability gap relative to frontier systems, and
it's the one where DeepSeek v4-Flash's knowledge of Lean idioms can actually pay off
once it's told *which* lemmas to compose.

**Effort:** Medium. Loogle integration is a few hundred lines; full Mathlib ingest is
the heavier path the repo already started.

### 2. Per-goal single-tactic proposal (HIGH IMPACT, LOW EFFORT)

**The gap this closes:** whole-sketch rewriting is fragile.

**Concrete plan:**
- Change the LLM turn contract from "rewrite the whole sketch" to "propose one tactic
  block for the first pending goal." The model returns just the tactic, not the file.
- Splice it in via the existing `SubstituteFirstSorry` — but targeted at the specific
  goal, not the first `sorry` lexically.
- This makes each LLM call cheap (small output → fewer output tokens → lower cost),
  focused (the model reasons about one goal), and combinable (failures don't revert
  siblings).

**Why it matters:** DeepSeek v4-Flash at `effort=none/low` is cheap and fast. Per-goal
proposals mean we can afford to try *many* candidate tactics per goal and keep whichever
compile — turning the loop from "one shot per turn" into "N candidates, Lean picks the
winner." That's the cheap version of tree search (below).

**Effort:** Low. The infrastructure (SubstituteFirstSorry, Lean compile per candidate,
fossilize-on-progress) already exists in the Tier 0.75 path. This generalizes it to
LLM-proposed tactics.

### 3. Best-first search over LLM branches (HIGH IMPACT, MEDIUM EFFORT)

**The gap this closes:** greedy descent can't recover from early dead ends.

**Concrete plan:**
- Each turn, ask the LLM for K=4–8 candidate next-tactics (single prompt, sampled at
  temperature 0.7 for diversity, or K distinct prompts).
- Compile all K against the current state. Keep the ones that reduce sorry; discard the
  rest. This is a branching factor-K tree search with Lean as the edge validator.
- Rank frontier nodes by sorry count + fossil-similarity (the planner's existing scoring),
  not just recency.
- Budget by node count, not turn count.

**Why it matters:** This is the structure that makes "many tries" actually compound.
The current swarm tries N independent episodes (each a greedy chain); a tree tries N
*branches* sharing a common prefix, so early good moves propagate. Same budget, more
effective coverage.

**Effort:** Medium. BestFirstGraphPlanner is the skeleton; wiring it to live LLM
proposals + the per-goal splicer from idea #2 is the work.

### 4. Tool-use: let the model call Lean mid-reasoning (MEDIUM IMPACT, HIGH EFFORT)

**The gap this closes:** the model can't inspect intermediate goal states during its own
reasoning — it only sees the result after a full compile.

**Concrete plan:**
- Switch Tier 3 (v4-pro, the reasoning model) to a tool-calling loop where `tactic_step`
  is a tool: model emits a tactic, gets back the resulting goal state, emits the next.
- This turns DeepSeek-v4-pro into an interactive prover (closer to how a human uses Lean).

**Why it's ranked lower:** DeepSeek v4-Flash (Tiers 1/2) is the workhorse and doesn't
need this. This only helps on the hardest problems where v4-pro is already engaged, and
those are exactly where 0/23 lives — so the upside is real but narrow.

**Effort:** High. Requires reworking the LLM client for tool-calls and a Lean tactic
stepper backend.

## Recommended sequencing

1. **Run the swarm as-is first** (what we're about to do) — establishes a baseline solve
   rate for the solvable-tier corpus with the *current* approach. Without this number,
   we can't tell if ideas #1–3 actually help.
2. **Idea #2** (per-goal single-tactic) — cheapest, unblocks #3.
3. **Idea #1** (premise retrieval via loogle) — biggest capability gap.
4. **Idea #3** (tree search) — compounds #1 and #2.

Idea #4 is a later-phase bet, not this cycle.

## What the swarm we're about to run will and won't tell us

- **Will:** baseline solve rate on 30 tractable stubs; whether graph-first + fossil
  retrieval + tiered escalation already cracks any; the citation-vs-independent split
  (integrity check).
- **Won't:** whether premise retrieval would help (we'd need to compare with idea #1
  implemented). That's a follow-up experiment, not this run.
