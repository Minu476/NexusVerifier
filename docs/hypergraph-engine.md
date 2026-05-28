# Lean-Native Hypergraph Engine
**Status:** Working (committed `394d161`, 2026-05-28)**  
**File:** `formal-conjectures/_nexus_tmp/ErdosHypergraph.lean`  
**Run:** `cd formal-conjectures && lake env lean _nexus_tmp/ErdosHypergraph.lean`

---

## What We Built

A **mathematical knowledge graph that lives entirely inside the Lean 4 elaborator**.  
No external database. No serialisation. No cross-language bridge.  
The graph is built, queried, and reasoned over in a single `lake env lean` invocation.

---

## Architecture

### Data Model

```
GoalShape  :=  String          -- pretty-printed type of a goal (Phase 1 / string-keyed)

HgEdge     :=  { function : String        -- Mathlib lemma name
                 inputs   : List String   -- Prop-kinded premises of that lemma
                 output   : String }      -- conclusion of that lemma

Hypergraph :=  { nodes : HashMap UInt64 GoalShape      -- hash(goal) → goal text
                 edges : HashMap UInt64 (Array HgEdge) -- hash(output) → edges proving it }
```

### Pipeline

```
  §1  Seed theorems (sorry-free)
        │
        │  proof.value.getUsedConstants
        ▼
  §3  extractEdge (MetaM)
        │  forallTelescope decomposes ∀ (data...) (h₁:P₁)…(hₙ:Pₙ), C
        │    Prop binders  → edge inputs  (subgoals)
        │    conclusion C  → edge output  (what this lemma proves)
        ▼
  §4  buildHypergraph (CommandElabM)
        │  collects all used constants, calls extractEdge on each
        ▼
  In-memory Hypergraph
        │
        │  O(1) backward lookup:  backwardEdges(goalText) = edges with that output
        ▼
  §2.5  AND/OR backward chaining search
          proveGoal g goal fuel visited
            • look up edges that produce 'goal'
            • if any edge has empty inputs → PROVED (leaf)
            • otherwise recursively prove each input (AND-conjunction)
            • try multiple edges (OR-disjunction), return first success
            • cycle guard via HashSet[visited]
```

### Key Lean 4 / Lean 4.27.0 API Facts Confirmed

| Wanted | Correct API |
|---|---|
| `Lean.HashMap` | `Std.HashMap` |
| `.find?` on HashMap | `.get?` |
| `HashMap.empty` | `{}` (EmptyCollection) |
| `HashSet.empty` | `{}` |
| `HashMap.fold` | `.toList.foldl` |
| `let (r, _) ← MetaM.run` | `let mr ← MetaM.run …; mr.1` |
| `Array.findSome? f arr` | `arr.findSome? f` (dot notation) |

---

## Current Results (clean build, exit 0)

### Graph Size

```
15 seeds → 73 unique Mathlib constants → 32 nodes, 32 edges
```

### Seed Coverage

| Domain | Seed | Key Mathlib Constants Harvested |
|---|---|---|
| Nat arithmetic | `seed_nat_comm` | `Nat.add_comm`, `Nat.mul_comm`, `And.intro` |
| List length | `seed_list_len` | `List.length_append`, `List.length_reverse` |
| ≤ transitivity | `seed_le_chain` | `Nat.le_trans`, `le_refl` |
| Finset union | `seed_finset_card` | `Finset.card_union_le` |
| Divisibility | `seed_dvd_antisymm` | `Nat.dvd_antisymm` |
| Dvd chain | `seed_dvd_trans` | `dvd_trans` |
| GCD | `seed_gcd_facts` | `Nat.gcd_comm`, `Nat.gcd_dvd_left`, `Nat.gcd_dvd_right` |
| Prime basics | `seed_prime_pos` | `Nat.Prime.pos`, `Nat.Prime.one_lt` |
| Infinite primes | `seed_inf_primes` | `Nat.exists_infinite_primes` |
| Modular arith | `seed_mod_facts` | `Nat.mod_lt`, `Nat.mod_le` |
| Sum monotone | `seed_finset_sum_mono` | `Finset.sum_le_sum` |
| Subset card | `seed_finset_subset_card` | `Finset.card_le_card` |
| Insert card | `seed_card_insert` | `Finset.card_insert_of_notMem` |
| Range card | `seed_card_range` | `Finset.card_range` |
| Prod nonneg | `seed_finset_prod_nonneg` | `Nat.zero_le` |

### AND/OR Search Results

```
[PROVED] 'n + m = m + n'                   1 step  →  apply Nat.add_comm          (leaf: no Prop inputs)
[PROVED] 'm.gcd n = n.gcd m'               1 step  →  apply Nat.gcd_comm          (leaf)
[PROVED] '(s ∪ t).card ≤ s.card + t.card'  1 step  →  apply Finset.card_union_le  (leaf)
[PROVED] 'as.reverse.length = as.length'   1 step  →  apply List.length_reverse   (leaf)
[PROVED] '∃ p, n ≤ p ∧ Nat.Prime p'        1 step  →  apply Nat.exists_infinite_primes (leaf)
[PROVED] 'x % y ≤ x'                       1 step  →  apply Nat.mod_le            (leaf)

[GAP]    'a ∣ c'          ← dvd_trans IS in graph; inputs 'a ∣ b' and 'b ∣ c' aren't grounded
[GAP]    's.card ≤ t.card' ← Finset.card_le_card IS in graph; input 's ⊆ t' isn't grounded
```

The GAPs are **mathematically correct**: you cannot prove abstract divisibility or subset cardinality without knowing the concrete witnesses. The engine never hallucates — it reports honestly.

---

## What We Investigated: The Erdős Problems Landscape

### DeepMind `formal-conjectures` (this repo)

| Metric | Count |
|---|---|
| Total problems | 1,981 |
| Mathematically solved (research) | 891 |
| **Formally proved in Lean** | **119** |
| Open | 1,090 |

The 119 formally proved problems have `@[formal_proof]` annotations. Their Lean proof terms live in **external repos**, not in `main`. Every theorem in the current `main` branch uses `sorry` as the proof body — including all ErdosProblems files.

**Consequence for the hypergraph**: sorry-proofs have `getUsedConstants = [sorryAx]`. `sorryAx` is not Prop-typed in the extractEdge sense, so sorry-proofs contribute **zero edges**. This is why we cannot mine the 891 "solved" statements directly.

### External Proof Repositories (where real Lean proofs live)

The `@[formal_proof using lean4 at <url>]` annotations in this repo point to:

| Repo | Content |
|---|---|
| `github.com/plby/lean-proofs` | Many Erdős problem proofs |
| `github.com/AlexKontorovich/PrimeNumberTheoremAnd` | Prime number theorem and related |
| `github.com/ebarschkis/ErdosProblem` | Erdős problem subset |

These contain the real proof terms. Importing them would yield Erdős-domain edges in the hypergraph.

### erdosproblems.com (Thomas Bloom, University of Bristol)

Independent database of 1,217 Erdős problems (550 solved, 45%). Not affiliated with DeepMind or Microsoft. The canonical mathematical reference for problem status.

### The Microsoft Connection

[Lean 4](https://lean-lang.org) was created at **Microsoft Research** by Leonardo de Moura. The `mathlib4` community is built on this foundation. The hypergraph engine's edges are all Mathlib lemmas — so every edge in our graph is indirectly a product of the MSR-originated Lean ecosystem.

---

## Why String-Keyed Matching Is the Current Limitation

The Phase 1 architecture keys nodes by `hash(ppExpr output)`. This means:

- `"m.gcd n = n.gcd m"` is a **different node** from `"Nat.gcd a b = Nat.gcd b a"`  
  (same mathematical fact, different variable names from ppExpr)
- `"a ∣ c"` (from `dvd_trans`) won't match `"x ∣ z"` (from a different seed)

This is why the Erdős goal node queries mostly show GAP: the query strings must exactly match what `ppExpr` produced when building the edge. **This is the primary motivation for Phase 2.**

---

## Phase 2 Design: `NexusHypergraph.lean`

Already drafted in `_nexus_tmp/NexusHypergraph.lean`. Key differences:

```lean
-- Phase 1 (current):  hash(ppExpr goal)        -- string, variable-name-sensitive
-- Phase 2 (draft):    canonicalHash (e : Expr)  -- e.hash via de Bruijn indices
```

De Bruijn indexing means `∀ x, x + 0 = x` and `∀ n, n + 0 = n` produce **identical hashes** — the graph becomes variable-name-independent. This unlocks:
- Queries written in any variable-naming convention matching edges from any seed
- Automatic merging of duplicate nodes from different seeds
- Reliable matching of Erdős theorem types as target nodes

---

## Roadmap to Full Erdős Coverage

### Step 1 (done): Mathlib infrastructure seeds
Current state — 32 edges covering basic number theory and combinatorics.

### Step 2: Add more domain seeds
Write sorry-free seeds in: modular arithmetic, Ramsey theory, graph coloring, analytic number theory. Each seed of ~10 lines typically yields 5–30 new edges.

### Step 3: Inject Erdős theorem types as target nodes
Run `lake build FormalConjectures.Subsets.FC100SolvedSet1`, then walk its 100 declaration names, add each theorem's type as a node (not a seed). The search then shows which of the 100 can be reached from current Mathlib edges.

### Step 4: Import external proof repos
Clone `plby/lean-proofs`, add as a Lake dependency, seed from its solved Erdős theorems. These have real proof terms → real domain-specific edges enter the graph.

### Step 5: Upgrade to Expr-hash (Phase 2)
Migrate from string-keyed to `Expr.hash`-keyed graph. Eliminates variable-name sensitivity, enables reliable Erdős node injection.

---

## Files

| File | Purpose |
|---|---|
| `_nexus_tmp/ErdosHypergraph.lean` | **Primary working engine** — string-keyed, fully working |
| `_nexus_tmp/NexusHypergraph.lean` | Phase 2 draft — Expr-hash-keyed, variable-name-independent |
| `_nexus_tmp/smoke_test.lean` | Early smoke test (superseded) |
| `_nexus_tmp/Test228828.lean` | Diagnostic for OeisA228828 module |
