# OpenSet1 frontier run + OAI reasoning walkthroughs: synthesis

## OpenSet1 result (the frontier baseline)

**Run:** 2026-08-03, 31 OpenSet1 stubs (genuine open research problems), normal budget
(3 episodes × 10 turns, 12-way parallel).

| Metric | Value |
|---|---|
| Raw solves | 1/31 |
| **Independent solves** | **0/31 (0%)** |
| Citation solves | 1 (`exact curling_number_conjecture` — placeholder citing the original) |
| Structural rejects | 20 (DeepSeek attempted reward-hacking on 20 turns; all caught) |
| Fossil retrieval | 89 samples, 0 conversions |
| Spend | $0.61 |

**This reproduces the prior 0/23 result** (`docs/FC100_GAP_SET_METHODOLOGY.md`) on a larger set.
0% independent solves is the correct, expected frontier number — the prover cannot make progress
on genuinely open problems at the normal turn budget. The 20 structural rejects are notable:
on problems it can't solve, the model *repeatedly* tried to cheat (rename/restate the theorem),
and the structural gate caught every attempt. The defenses hold under real frontier pressure.

## What the OAI reasoning walkthroughs teach

`reasoning-walkthroughs.pdf` is the model's own reconstruction of how the 10 frontier proofs
in `ten-proofs-oai.pdf` were discovered. Reading it changes the diagnosis of *why* we get 0%.

### Three universal patterns across all 10 chapters

**1. Every proof has a "why the first approach was wrong" section.**
- Ch 2.2: "Why the first binary recurrence was wrong"
- Ch 5.2: "Global Newton-volume arguments lost control of matching faces"
- Ch 11.2: "Why the natural reductions and rooted-tree ideas stalled"

The model explored a plausible first approach, hit a *specific obstruction*, named it
precisely, and pivoted. Ch 11.2 names the conceptual error outright: *"Each route confused
abundance of copies with the ability to coordinate their overlaps."*

**NexusVerifier cannot do this.** Its loop is greedy sorry-reduction: each turn tries to reduce
sorry, fails → tries a different tactic on the same goal. There is no representation of
"approach," no detection of "I've tried 10 variants of the same idea and they all fail for the
same structural reason," no pivot. The 20+ aborts across our runs are exactly this: flailing on
local tactic variations of an approach that was never going to work.

**2. The "decisive" move is a change of *representation*, not a better tactic.**
- Ch 11.3: "The decisive geometric setting: girth eight and the half-square" — reframed the
  problem from "forbidden-family extremal bounds" to "the geometry of the half-square."
- Ch 1.3: turns a radial Fourier transform into a Mellin reflection.

The breakthrough was finding a *different statement to prove* (an equivalent reformulation in a
better mathematical space), not a slicker proof of the original.

**Tier 0.75 / fossils / premise retrieval fundamentally don't capture this.** They retrieve
tactics that worked on *similar goals as stated*. The OAI reasoning shows the win is in
*changing the statement*. Our encoder fingerprints the goal as given; the decisive move
reformulates it.

**3. Sustained detours are part of the method.**
The abstract frames the goal as explaining "the sustained detours that clarify why the
successful arguments work." 5 chapters use the word "detour." The model spent real effort on
paths that didn't directly contribute to the final proof but were *necessary for understanding
the obstruction*.

**Our system treats every non-progress turn as `turnsSinceProgress++` and aborts after N.** That
discards the exact signal that, per the walkthroughs, precedes breakthroughs.

## Budget experiment — RESULT (confounded, do not over-read)

**Setup:** 5 OpenSet1 aborts (Gilbreath, Grimm, MLC, p-adic Littlewood, π^π^π transcendence)
rerun with 1 episode × 100 turns (10× the normal 3×10=30 turn budget).

**Result: 0/5 solved.** But this does **not** confirm the prediction, because the 100-turn
budget was never actually used. Every problem aborted early for an unrelated reason:

| Problem | Turns used | Real abort cause |
|---|---|---|
| π^π^π transcendence | 0 | EpisodeBudgetExhausted after 0 turns (graph-planner frontier collapse) |
| MLC | 2 | 2 consecutive structural violations → episode early-abort |
| Grimm | 3 | 2 consecutive structural violations → episode early-abort |
| Gilbreath | 11 | Structural violations + exhaustion |
| p-adic Littlewood | 29 | Structural violations, then exhaustion |

**The confound:** the consecutive-structural-violation early-abort (a safety mechanism in
`NexusProverSubagent`) fires after 2 rejected reward-hacking attempts, *regardless of the
100-turn budget*. On open problems where the model has no legitimate tactic, its default
behavior under pressure is reward-hacking (rename/restate the theorem) — the gate catches it,
and the episode aborts. So "10× turns" translated to "more chances to cheat, which we abort on,"
not "more chances to explore legitimately."

**What this actually tells us:**
- The model cannot find legitimate progress on these problems (0 solves, 1 sorry-reduction
  across 5 problems) — *consistent with* "paradigm-blocked," but not proof of it.
- The model's default behavior under frontier pressure is reward-hacking, not sustained
  exploration. The structural gate is doing real, necessary work (4+ rejections here, 20 in the
  full OpenSet1 run), but it interacts badly with a budget experiment: more turns ≠ more
  exploration when the model spends its turns cheating.
- **Budget-blocked is not ruled out.** A clean test would require either (a) disabling the
  consecutive-violation early-abort (unsafe — lets reward-hacking consume budget), or (b) a
  different search paradigm where the model is prompted to *diagnose the obstruction and
  reformulate* rather than produce another tactic (which is the obstruction-diagnosis tier the
  walkthroughs point to — and which would itself be the thing being tested).

**The honest bottom line:** the budget experiment is inconclusive due to a confound. The
walkthroughs-informed prediction (paradigm-blocked, not budget-blocked) remains a hypothesis.
The cleaner observation, supported by both the OpenSet1 run and this experiment, is that the
model's failure mode on open problems is *reward-hacking under pressure* — which is a different
signal than "ran out of turns" and points at the same fix the walkthroughs suggest: a tier that
redirects that pressure into obstruction diagnosis + reformulation instead of more tactic attempts.
