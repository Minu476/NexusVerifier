# Session handoff — 2026-08-04 → next session

**Written by:** Opus 5, end of the 2026-08-04 session · **For:** whoever picks this up next.
**Supersedes** `SESSION_HANDOFF_2026-08-03.md` (that one's list is done; keep it for the history).

The headline of this session: **the pivot-gate question is closed.** Four A/B rounds, each one
built to remove the objection raised against the last, all null. Everything below is either the
evidence for that, or the loose ends it left behind.

---

## 1. Do this first (one action, ~1 minute)

**`ef604df` is committed locally and not pushed.** It is the round-4 writeup plus its two bench
JSONs — docs and data only, no code.

```bash
cd /Users/nassertowfigh/Projects/NexusVerifier
git log --oneline origin/master..HEAD     # expect exactly ef604df
git push origin fix/bench-hang-diagnosis
gh pr create --base master --fill          # PRs #10–12 all merged from this branch already
```

Nothing else in either repo is waiting on a push.

---

## 2. State of both repos

### Topos (`/Users/nassertowfigh/Projects/Topos`)

`main` at `7e0d18c`, **in sync with origin, nothing unpushed**. Full suite: **233/233 pass**
(verified at end of session, Release).

The working tree is **dirty with work that is not mine** — docs reorganisation into `docs/process/`,
a new `.github/workflows/build-test.yml`, `.gitignore`/`README`/`AGENTS.md` edits, and staged
deletions of `docs/Documentation.html` / `.pdf`. This looks like GLM's parallel documentation pass.
**Do not commit it blind.** Read the diff and confirm with Nasser whose it is before touching it.
Note in particular that deleting the generated PDF is plausible-but-check: `docs/build_pdf.py`
regenerates it, and the last session specifically fixed the PDF's TOC hyperlinks.

Published to NuGet: `0.2.0-m.11` (Hypergraph, Knowledge), `0.2.0-m.8` / `0.2.0-m.10` for the other
two. The old `0.1.0-m*` set is unlisted (soft — an exact pin still restores). **The version-scheme
issue is closed; don't re-litigate the `-m.N` dot, it is load-bearing** (undotted `m11` sorts below
`m8` under SemVer's ASCII rule, which is what made the packages unreachable).

### NexusVerifier (`/Users/nassertowfigh/Projects/NexusVerifier`)

Branch `fix/bench-hang-diagnosis`, one unpushed commit (§1). Default branch is **`master`**, not
`main`. PRs #10, #11, #12 merged this session.

Tests: **162/164 pass.** The 2 failures are **environmental, not a defect** — I verified this:

```
NEXUS_LEAN_PROJECT=/Users/nassertowfigh/Projects/DeepMind-Nexus-Challenge/formal-conjectures
```

is exported in the shell profile and points at a stale copy whose Lean build artifacts are gone
(`unknown module prefix 'FormalConjectures'`). Re-run against the in-repo submodule and all 10
LeanOracle tests pass:

```bash
NEXUS_LEAN_PROJECT=/Users/nassertowfigh/Projects/NexusVerifier/formal-conjectures \
  dotnet test NexusAgent/NexusAgent.Tests/NexusAgent.Tests.csproj -c Release
```

**Fix the exported variable rather than the tests.** Also: the `formal-conjectures` submodule
pointer is dirty (`a86f430` → `eb397ad`, "commits not present" — the local checkout has moved
ahead of what origin knows). Resolve that deliberately; don't let it ride along in an unrelated
commit.

---

## 3. The result that closes the pivot-gate question

Four rounds. Each removed the objection raised against the previous one. **Every round null.**

| Round | Corpus | Control | Treatment |
|---|---|---|---|
| 1 | original | 0 independent | 0 independent |
| 2 | + citation detection + keep-searching | 1 | 1 |
| 3 | prevented (7/17 hard-deleted) | 1 | 1 |
| 4 | **fully prevented (17/17 hard-deleted)** | **1** | **1** |

The single solve is the same problem (`tripleProduct_const`) with a byte-identical three-line proof
in all seven runs. Treatment costs +28% to +52% and its reformulations compile ~7% of the time.

**Recommendation: stop investing in the pivot machinery.** It has had four rounds, two targeted
bug fixes, and a corpus purpose-built to give it a fair test. Full writeups:
`data/results/PIVOT_AB2_NULL_RESULT_2026-08-04.md`, `PIVOT_AB3_PREVENTED_2026-08-04.md`,
`PIVOT_AB4_FULLY_PREVENTED_2026-08-04.md`.

### The finding actually worth carrying forward

The citation instinct **relocates** under each mitigation instead of converting into proof search:

1. cite the target (rounds 1–2) → blocked by `CitationDetector`
2. rewrite the declaration signature (round 3 — 9 of 15 reformulations) → blocked by the structural gate
3. cite a `sorry`-backed *dependent* left by the deletion repair (round 4 — 10 control / 17 treatment) → blocked by `#print axioms`

That is a property of the prover under evaluation pressure, and it is a more useful thing to know
about this system than anything the gates measured. **If you build the next experiment, assume the
model will find the fourth form and design the measurement to catch it before you trust the count.**

### What the rounds also established

- **Round 1's evaluation was 100% contaminated** — every raw "solve" was a citation. It isn't now.
- **The corpus is far harder than the raw numbers suggested.** With citation removed, 16 of 17 are
  unsolved by either arm. That is the honest baseline.

---

## 4. Known measurement gaps — read before quoting round 4

- **`unknown identifier` counts are not measurable from the bench log.** The log carries only
  LeanOracle's summary lines, not Lean's error text. Round 4 can say citation succeeded zero times;
  it cannot say how often it was *attempted*. If that number matters to the next experiment, log
  the Lean error text first.
- **Treatment completed 16 of 17.** `not_isThick_of_finite` died on a transient DeepSeek
  `HttpRequestException: Connection reset by peer`. It aborted in control and in every prior round
  too, so it is not a plausible lost solve — but the arms are 17 vs 16 and that is stated, not
  smoothed.
- **Always use a fresh `--source` tag.** `Program.cs`'s already-solved check silently skips problems
  marked `Solved` in Neo4j. The round-3 tags had 2 such problems; reusing them would have corrupted
  round 4 invisibly. This has nearly bitten twice.
- Both arms share one Neo4j and control runs first, so fossil carryover is directional.
- N=17, one run per arm. The *direction* is consistent across four rounds; no single round is
  statistically powered.

---

## 5. Two process lessons that cost real time this session

Both are recorded because they were expensive, not to be self-flagellating.

**The bench "hang" was never a hang.** Four runs were killed as "deadlocked under parallel load" and
hours went into a suspected Neo4j async-session deadlock. The logs said otherwise: one request in
flight, every other response 200 with p99 under 500ms, and the process killed roughly a minute
*before* HttpClient's 5-minute timeout would have fired and let it continue. A single slow request
is parallelism-independent — which is exactly why dropping to parallelism 3 "didn't help" and
reinforced the wrong diagnosis. The fix is observability, not concurrency:
`TieredLlmRouter.WithSlowRequestHeartbeat` now warns at 45s then every 60s so nobody kills a healthy
run again. **Do not kill a run on the strength of a quiet log.**

Note that I also got this wrong on the way: I wrote a pipe-EOF fix first, then built a standalone
reproduction that **disproved my own hypothesis** (the read *is* cancellable on macOS). The
reproduction is what made the real answer findable. Build it before you ship the fix.

**Verify the measurement before trusting the comparison.** Three separate claims this session
collapsed under checking: a reported "10%→67% compile-rate improvement" (arithmetic — real figures
were 27% and 6%); "first evidence the pivot machinery works" (the control found the identical
proof); and my own "Arm B was killed" (a `ps aux | grep` false negative — the run had finished; use
`pgrep -f`). I made that last one twice and it cost a redundant run.

---

## 6. If you want to keep going, in priority order

1. **Push `ef604df`** (§1).
2. **Fix `NEXUS_LEAN_PROJECT`** in the shell profile and resolve the submodule pointer (§2).
3. **Decide the pivot gates' fate explicitly.** They are still behind `PivotGatesEnabled` /
   `NEXUS_PIVOT_GATES` and default off. Either delete them or write down why they're being kept —
   leaving dead machinery behind a flag is how the next person re-runs this experiment.
4. **If you want a new direction, attack the real baseline, not the gates.** 16 of 17 unsolved is
   the number to move. The relocation finding (§3) says the lever is not "block one more shortcut."
5. **Topos ↔ NexusVerifier integration** is live and working: `NexusAgent.Core` project-references
   `external/Topos/src/Topos.Hypergraph`, and `ToposTacticStore` (race fixed this session — the
   `GoalCount`/`TacticEdgeCount` getters now take `_writeLock`) is the goal/tactic graph. The
   pending X/Twitter post about Topos was deliberately held until there was a real integration
   benefit to point at; the honest current answer is that the integration works but the A/B did not
   produce a win to advertise.

---

## 7. Context worth carrying

- **Nasser's standing instruction is to work continuously without pausing for approval**, and he
  said "you are the only one in charge." Pauses to ask permission mid-task were corrected four
  separate times. Exceptions where stopping *was* right: a credential about to enter a public repo.
- **NexusVerifier is a public repo.** `run_swarm.sh` had a hardcoded Neo4j password; it now requires
  `NEXUS_NEO4J_PASSWORD` and exits if unset, and the history was scrubbed. Check before every push.
- **Raw bench logs are deliberately not committed** (~1.6 MB). Writeups + `bench-*.json` +
  `bench-*.meta.json` only. `data/results/*.log` in the working tree is expected to stay untracked.
- Neo4j for these runs: `bolt://localhost:27687`, database `neo4j`. GDS oracle for Topos is a
  separate container (`topos-gds-oracle`, ports 17687/17474).
- Reproduction commands for every round are in the Reproduction section of each writeup.
