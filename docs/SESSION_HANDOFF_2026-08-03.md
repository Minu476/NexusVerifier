# Session handoff — 2026-08-03 → next session

**Written by:** Opus 5, end of the 2026-08-03 session · **For:** whoever picks this up next
(GLM is rate-limited until the morning; Sonnet can execute this list as written).

**Priorities below are set by the A/B outcome, not by continuing the prior plan.** The null result
changes what is worth doing next — read §"Context worth carrying" before reordering.

---

## State

### Topos (`/Users/nassertowfigh/Projects/Topos`) — clean, nothing pending

`main` at `7e0d18c`, 233/233 tests pass, working tree clean. Three commits this session:

1. M11 phase 1 algorithms (`Centrality`, `PageRank`, `DirectedScc`) — live-verified against a real
   Neo4j+GDS instance, not just soft-skipping tests.
2. NuGet version-scheme fix — the undotted `-mN` suffix sorted wrong (`m11 < m8` in ASCII order),
   which made the freshly-published packages unreachable via `--prerelease`. Fixed to `-m.N` **and**
   base `0.1.0 → 0.2.0` (dotting alone does not clear the already-published set).
3. `mode=unlist` added to the publish workflow (no local API key exists — publishing is OIDC-only
   inside Actions).

NuGet now serves `0.2.0-m.11`; the mis-ordered `0.1.0-m*` versions are unlisted (soft — still
restorable by exact pin, so nobody who pinned them breaks).

### NexusVerifier (`/Users/nassertowfigh/Projects/NexusVerifier`) — **everything uncommitted**

Contains GLM's pivot gates, Sonnet's build fix + config + tests + `citation_audit.py` bug fixes,
and the A/B results. Nothing has been committed or pushed.

### The A/B result (verified independently against the raw JSON)

17 problems per arm — the "baseline did not independently solve" residual.

| | Control (gates off) | Treatment (gates on) |
|---|---|---|
| raw "solves" | 5 | 7 |
| citation exploits | 5 | 7 |
| **independent solves** | **0** | **0** |
| turns | 260 | 261 |
| structural rejects | 4 | 1 |
| cost | $0.444 | $0.508 |

**Null result.** No evidence the pivot gates improve genuine proof-search capability. The treatment
arm found two *more* citation exploits, which is not an improvement.

Small N — this is "no signal detected," not "proven no effect."

**The `4 → 1` structural-reject drop is an instrumentation artifact, not behavior.** The counter
only increments when an *episode terminates* with `StructuralGateRejection`
(`NexusOrchestrator.cs:241`). With gates on, the 2nd violation instead triggers a diagnosis that
**resets the consecutive-violation counter** (`NexusProverSubagent.cs:551-554`), so it takes roughly
double the raw violations to increment — or never increments at all. Do not cite this number as
evidence of reduced cheating.

---

## Task 1 — Commit the NexusVerifier work (do this first)

A full session of work is uncommitted and fragile. Commit **on a branch**; do not push.

Include: GLM's two pivot gates + `PivotGatesEnabled` flag, the `Program.cs` scoping fix,
`MaxDiagnosesPerEpisode` config + parametrized test, the two `citation_audit.py` bug fixes, and
`data/results/PIVOT_AB_2026-08-03.md`.

The commit message must state the outcome honestly: the gates are implemented and fire, and the
A/B is a **null result on independent solves**. Do not describe the structural-reject drop as a
behavioral improvement — say it is a counter artifact and reference the finding above.

**Done when:** work is on a branch, `dotnet build` clean, test suite at its known state — 142 tests,
140 pass. The 2 `LeanOracleTests` failures are environmental (an inherited `NEXUS_LEAN_PROJECT`
pointing at an unbuilt project path), not code.

## Task 2 — Resolve the reformulation-retry discrepancy

The log prints `reformulation attempt 1 did not compile — retrying with error feedback`, but there
is reportedly a TODO in the code admitting the compile error is **not** actually fed back into the
retry prompt. If true, the message is lying and the retry is a blind resample — which would explain
**10 reformulations fired, only 1 compiling (10%)**.

Read the reformulation retry path in `NexusOrchestrator.cs` (~lines 319-390) and determine whether
the Lean error text actually reaches `BuildReformulationRequest`. Then either wire it through
properly, or fix the log message to stop claiming something it does not do. Both are acceptable;
silently leaving a misleading log is not.

**Done when:** the log statement and the actual behavior agree — with a test if you wire the
feedback through.

## Task 3 — Promote citation detection into a solve-time gate

**Highest-value item.** This is a *metric-integrity* fix, not a capability one.

`citation_audit.py` currently catches citation exploits **post-hoc**. The orchestrator still records
them as `Outcome=Solved`, so every raw number needs manual auditing before it means anything — and
that audit script itself had two bugs found this session that made it miss nearly everything.

The exploit shape: for target `foo`, the model emits

```lean
theorem g_foo_new (args) : <same statement> := by exact foo args
```

The original `foo` is still in scope in the same namespace, so this compiles, has clean
`#print axioms`, and passes the structural gate — the statement is unchanged, so nothing structural
was violated. Observed forms from this session:

```
exact ame_2_exists hd            exact eqSystem4_has_solution_d2
exact ame_3_exists hd            exact f_undefined_at_2
exact flt_of_beal_conjecture H
```

Move the check inline: on a successful compile, if the proof term is a trivial application of the
target's original declaration, record a distinct outcome (`CitationExploit` or similar) rather than
`Solved`.

Two design points — weigh both and state your choice in the writeup:

- **Detect vs. prevent.** Detecting is a gate. *Preventing* — removing or renaming the original
  declaration when the problem file is built, so citation is impossible — is stronger and closer to
  what the benchmark should have done originally. If prevention is a large change, do detection now
  and write prevention up as a proposal.
- **Do not over-reject.** Citing genuine *helper lemmas* is legitimate mathematics. Only citing the
  target itself (or a trivial alias) is the exploit. Get this boundary right and test both sides.

**Done when:** a citation exploit is no longer reported as `Solved`; unit tests cover both the
exploit and a legitimate helper-lemma citation; the known-correct baseline split (13 independent /
4 citation) still reproduces.

## Task 4 — Bump `external/Topos` and adopt `DirectedScc`

The submodule is pinned at `fe03513` (M8). Topos is now `0.2.0-m.11` and ships `DirectedScc` — the
generic, tested replacement for the hand-rolled per-DFS-path cycle guard described in
`docs/NEXUS_VERIFIER_INTEGRATION_FINDINGS.md` finding #4. Landing that adoption also closes Topos's
own M11 exit criterion (spec §7).

Do this **after** Tasks 1-3 so it does not tangle with the gate work.

---

## Do not

- **Do not run more bench experiments.** The null result stands; there is no new hypothesis to test
  yet, and compute costs real money. One earlier session burned a run on a confounded design
  (early-abort defeated the budget variable), and this session burned another on a bad process
  check. Have a falsifiable question before spending again.
- **Do not push, publish, or unlist anything.**
- **Do not modify the structural gate's rejection criteria** — that is a soundness mechanism.
- **Do not report any capability improvement** unless it appears in **independent** solves.

---

## Context worth carrying

The dominant failure mode on the hard-residual corpus is **citation exploitation**, not
stuck-approach. The model reaches for `exact <original>` long before the pivot gates can engage.

That is why the gates showed nothing: they were built for a failure mode that is not the binding
one on this corpus. Any future capability work should target that ordering — close the citation
hole first, then re-measure what the real failure modes are on a corpus where "Solved" actually
means solved.

Two process lessons from this session, both learned the expensive way:

1. **An experiment whose predicted outcome is identical under both arms measures nothing.** The
   earlier open-problem pivot test could not have failed — zero independent solves was the expected
   result with or without gates.
2. **Verify the measurement before trusting the comparison.** The structural-reject "improvement"
   dissolved on inspection because the intervention changed *when the counter increments*. Check
   instrumentation equivalence before reading any before/after delta.
