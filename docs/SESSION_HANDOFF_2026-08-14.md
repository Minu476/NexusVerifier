# Session handoff — 2026-08-14 (mid-Phase-3 cutoff)

**Written:** during the session, after a rate-limit cutoff mid-work — this doc now
exists because the earlier plan deferred the handoff to Phase-5 closeout, which
assumed a clean phase-boundary ending. Abrupt cutoffs break that assumption, so
from here on handoff state gets written at every phase boundary, not just closeout.
**Supersedes:** `SESSION_HANDOFF_2026-08-04.md` (its §6 list is now mostly done — see status).

**Plan in force:** the v2 catch-up plan (Opus-5-reviewed), archived as
`data/results/TRANSITION_DUMP_AB_DESIGN_2026-08-14.md` §0 context + this session's PRs.

---

## 1. Status by phase

| Phase | State |
|---|---|
| 0 — publish | **DONE.** PR #13 merged (round-4 writeup + 08-04 handoff). gitleaks-clean. |
| 1 — housekeeping | **DONE, PR #14 OPEN** (`chore/catchup-hygiene`, 6 commits). Backup tarball verified; Prevent corpus vendored; both submodules registered; `~/.zshrc` fixed; **measured 162/164** (2 = stale-olen, resolved by the port); A/B design doc pre-registered with McNemar MDE. |
| 2 — gate removal | **DONE, PR #15 OPEN** (`fix/remove-pivot-gates`, stacked on #14). Gates deleted, structural gate + CitationDetector kept, loud solved-skip WARN, `docs/PIVOT_GATES_POSTMORTEM.md`. **156/158** (same 2 olen failures). Lessons-graph write DEFERRED to Phase 5 (FSDE creds live in keychain — `.env.keychain`). |
| 3 — v4.33 port | **~90% DONE.** See §2 — this is where work stopped. |
| 4 — dump + A/B | Not started. Everything it needs is ready (walker compiles on v4.33 unchanged — validated in zeta-23-lean). |
| 5 — closeout | Not started. |

## 2. Phase 3 exact state (resume here)

**Done and pushed** — submodule branch `nexus-v4.33` on fork `Minu476/formal-conjectures`
(remote `fork`; upstream push DISABLED via bogus push-URL; branch expiry **2026-09-30**):

- `FormalConjecturesForMathlib`: **100% green on v4.33.0-rc2** (8849 jobs, 0 errors). 24 files ported.
- `FormalConjecturesUtil` + linters: green (compat instances added at Util root; dead
  `set_option linter.*` suppressions deleted; `Lean.Linter.Basic` meta import).
- Corpus: fixed InverseGalois, Erdos36, FirstProof4 + 7 files via the compat instances.

**Remaining — 7 critical files** (the exact transitive blockers of
`FormalConjectures.Subsets.FC100SolvedSet1`, which the LeanOracle tests and every
`data/swarm_*` bench stub import):

1. `FormalConjectures/Arxiv/1308.0994/BoxdotConjecture.lean` — missing `quot_precheck` macro for `SetNotationForOrder.subsetStx'` + instance failure
2. `FormalConjectures/Arxiv/1609.08688/sIncreasingrTuples.lean` — simp no-progress + unsolved (2 sites)
3. `FormalConjectures/ErdosProblems/1054.lean` — final `omega` fails: **diagnosed** atom mismatch between `hsum : 5 = ∑ i ∈ Finset.range k, …` and `hsub : … ≤ ∑ i < k, …` (trace shows real contradiction exists; omega sees two atoms — likely sugar/instance difference). Next attempt: force both sums to one syntactic form before omega (e.g. restate hsub via `Finset.sum_congr rfl` or cast both to ℤ explicitly).
4. `FormalConjectures/ErdosProblems/141.lean` — 2 unsolved goals
5. `FormalConjectures/Mathoverflow/10799.lean` — 2 instance failures + 2 unsolved
6. `FormalConjectures/OEIS/6697.lean` — 1 unsolved
7. `FormalConjectures/WrittenOnTheWallII/Test.lean` — 1 application type mismatch

**The other ~43 broken corpus files are NOT in the pipeline's transitive closure** — leave
them; upstream will fix them when google-deepmind itself moves past v4.27. Do not gold-plate.

**Then:** (a) verify `lake build FormalConjectures.Subsets.FC100SolvedSet1` green;
(b) parent-repo step: repoint `formal-conjectures` gitlink to fork commit + change
`.gitmodules` URL to the fork (commit on `fix/remove-pivot-gates` or a new stacked branch);
(c) run the LeanOracle test suite with `NEXUS_LEAN_PROJECT` → expect the 2 stale-olen
failures GONE → 158/158.

**Useful port lore (paid for in time, do not relearn):**
- v4.33 strictness: universe inference in class/structure fields → use `Type*` auto-bind or restructure as explicit `∃ … : Prop` (InverseGalois pattern).
- `SimpleGraph` fields are now `Std.Symm`/`Std.Irrefl` wrappers → `symm := ⟨fun u v h ↦ …⟩`.
- `Sym2.mk` takes two args, not a pair; `Fintype (Sym2 α)` moved to `Mathlib.Data.Finset.Sym`.
- `Turing.eval` → `StateTransition.eval`; `Mathlib.Data.Nat.PartENat` is gone; `ENat` from `Mathlib.Data.ENat.Lattice`.
- `Nat.factorization` lost native-decide; `decide +native` sanity examples → drop with port note.
- Fast iteration: `lake env lean <one file>` (single-file compile ~30s) beats `lake build` loops.
- Submodule gitlink commit for 8adf5b7 + Prevent vendoring already on master-bound PRs.

## 3. Phases 4–5 reminders (from the approved plan)

- Walker `NexusLean/NexusLean/InfoTreeWalker.lean` **already compiles on v4.33 unchanged** (validated via a probe module in zeta-23-lean — `Zeta23.InfoTreeWalkerProbe`, deleted after).
- A/B design is pre-registered (`TRANSITION_DUMP_AB_DESIGN_2026-08-14.md`): directional run, store isolation via cold dump/restore container on 27690 + verification query, fresh tags `TDABC1/TDABT1`, 08:00 abort criterion, **Visualizer don't-launch rule** (TradingSystem SIGKILL incident — verify Gemini's fix is deployed before any unattended run).
- Lessons graph (FSDE `knowledge_recall`) write pending — needs FSDE Neo4j creds (`.env.keychain`); flag to Nasser.
- Open items for Nasser: fsde-model-catalog-fix/ disposition, Topos repo dirty doc-tree ownership, Visualizer fix confirmation, Opus's duplicate-identity/canonicalization observation.

## 4. Repo/branch map

```
master            ← PR #13 merged
chore/catchup-hygiene  → PR #14 (open)   — Day-1 housekeeping
fix/remove-pivot-gates → PR #15 (open, base #14) — gate removal + THIS handoff doc
formal-conjectures submodule: branch nexus-v4.33 @ fork (Minu476), upstream main = 8adf5b7
zeta-23-lean submodule: pinned 3635e74 (upstream pristine)
```

Test commands (fresh shells lose env — always set):
```bash
export NEO4J_URI=bolt://localhost:27687 NEO4J_USERNAME=neo4j NEO4J_DATABASE=neo4j \
       NEO4J_PASSWORD="$(docker inspect nexus-neo4j --format '{{range .Config.Env}}{{println .}}{{end}}' | grep '^NEO4J_AUTH=' | cut -d/ -f2-)" \
       NEXUS_LEAN_PROJECT=/Users/nassertowfigh/Projects/NexusVerifier/formal-conjectures
cd NexusAgent && dotnet test NexusAgent.Tests/NexusAgent.Tests.csproj -c Release
```
