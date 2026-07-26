# HyperedgeComposer — reminder notes

**Status: preserved, not reviewed, not merged.** This branch
(`preserve/hyperedge-composer`) was found uncommitted in a second local
NexusVerifier checkout, from a work session with no context carried into the
one that found it. This doc exists so a future session doesn't have to
re-derive what the code does from scratch — everything below is from reading
the actual diff and running the tests, not from original design intent.

## What it is

A **Tier-0, LLM-free proof-closing step**, added to `BestFirstGraphPlanner`.
Before the planner spends an LLM call or a graph-vector proposal on a node,
it now first asks: *can the stored hyperedge database close this goal on its
own, by AND-join composition of lemmas already in the store?*

- `HyperedgeComposer.TryComposeAsync(pendingGoalText, maxDepth, ct)` —
  strips the Lean "unsolved goals" text down to the bare conclusion
  (`ExtractConclusion`), looks up matching `HyperedgeRecord`s by exact
  output-text match (new `INeo4jClient.GetHyperedgesByOutputAsync`), and does
  a bounded depth-first AND-join search: if an edge has premises, every
  premise must itself be resolvable within the remaining depth budget.
  Returns a `DerivationNode` tree on success, `null` if the store can't
  close it.
- `DerivationNode` — the resulting derivation tree; `IsLeaf` when an edge
  needed no premises, `Depth` computed recursively.
- `HyperedgeComposer.BuildTacticSketch` — turns a `DerivationNode` into a
  Lean term-mode tactic string (`exact lemma (premise1) (premise2)`),
  parenthesizing compound sub-terms.
- **Wired into `BestFirstGraphPlanner.TrySolveAsync`** as the first thing
  tried on every node expansion: build the composed sketch, compile it via
  `ILeanOracle`, and either accept it as `Solved` (if fully proved) or push
  it onto the frontier with high priority (if it reduced the sorry count).

## Verified (by this note's author, 2026-07-26)

- **Builds clean** against this branch's own state (predates the Topos
  submodule work — `external/Topos`/`.gitmodules` don't exist here, that's
  expected for a branch forked before that merged, not a bug in this diff).
- **12/12 pure unit tests pass** (`HyperedgeComposerTests.cs`) —
  `ExtractConclusion` (bare goal, with hypotheses, no turnstile),
  `BuildTacticSketch` (leaf, one-premise, nested-parenthesization), and
  `TryComposeAsync` against a mocked `INeo4jClient` (leaf edge, no edge,
  two-premise AND-join, one unsolvable premise, max-depth-zero, and a cyclic-
  premise guard that terminates instead of looping).

## NOT verified — check before trusting this

- **No live integration test.** Nothing here exercises
  `GetHyperedgesByOutputAsync` against a real Neo4j instance, or the
  `BestFirstGraphPlanner` Tier-0 wiring end-to-end against real Lean output.
  The unit tests mock `INeo4jClient` entirely.
- **Possible structural-gate gap on the success path.** In
  `BestFirstGraphPlanner.cs`, the `Solved = true` return (composed sketch
  fully proved) does **not** call `SketchValidator.IsStructurallyValid` —
  only the "partial progress, push to frontier" branch a few lines below it
  does. Every other success path in this codebase (the LLM-driven prover,
  the graph-proposal tiers) goes through that gate specifically because
  skipping it was the root cause of a real reward-hacking bug found and
  fixed earlier this cycle (`fix/prover-soundness-bugs`, PR #1). **This
  needs to be checked and almost certainly fixed** before this branch is
  trusted with anything — it's exactly the class of gap that's bitten this
  project before, not a hypothetical.
- **Exact-string-match lookup.** `GetHyperedgesByOutputAsync` matches
  `e.output = $output` — no whitespace normalization, no semantic/embedding
  similarity. Two goals that are logically identical but pretty-printed
  slightly differently (extra parens, different variable names Lean chose)
  will silently miss each other. May be fine (a first cut deliberately
  scoped to exact matches) or may explain a low hit rate if this was ever
  run — no data either way, wasn't tested against a live store.
- **The rest of the diff isn't purely about this feature.** The preserved
  commit also bundles: a `nexus solve` CLI flag expansion (`--source`,
  `--episodes`, `--max-turns`, `--graph-first`, `--no-llm-fallback`,
  `--planner-max-expansions`, `--planner-branch-factor`,
  `--planner-neighbor-k`), and an unrelated `logDir` computation fix
  (`AppContext.BaseDirectory` instead of
  `Assembly.GetExecutingAssembly().Location`, which is the right fix for a
  single-file-published binary but has nothing to do with hyperedges). Worth
  splitting these apart if this ever becomes a real PR.
- **`HyperedgeComposer? _composer`** is declared nullable in
  `BestFirstGraphPlanner`, but the constructor parameter that sets it
  (`HyperedgeComposer composer`) isn't — DI (`Program.cs` registers it as an
  unconditional singleton) means it's never actually null in practice. The
  `?` looks like a leftover from an earlier draft rather than an intentional
  optional-dependency design; worth cleaning up or explaining.

## If picking this back up

1. Fix (or explicitly justify not fixing) the missing `IsStructurallyValid`
   check on the `Solved = true` path — this is the one thing that must not
   ship as-is.
2. Write at least one integration test against a real Neo4j instance seeded
   with a couple of `HyperedgeRecord`s, exercising the actual planner path,
   not just the mocked unit tests.
3. Decide on the exact-match-vs-normalized-lookup question deliberately,
   rather than by default.
4. Split the CLI-flag and `logDir` changes into their own commit if this
   goes to a real PR — they're unrelated to hyperedge composition and
   shouldn't ride along silently.
