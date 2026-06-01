# Erdős Problems — Formal Proofs Reference

A reference list of Erdős problems that have been formally verified in Lean 4 / Mathlib,
grouped by solver. Each entry summarises what the problem asks mathematically.

---

## Solved by Google DeepMind AlphaProof

**#12** — **Divisibility in good sets** (AMS 11 · Number Theory)
Call a set *A* ⊆ ℕ *good* if it is infinite and contains no three distinct elements *a < b, c*
with *a* | (*b* + *c*). DeepMind proved: **(i)** there exists a good set *A* with
$\liminf_{N\to\infty} |A \cap [1,N]| / \sqrt{N} > 0$, and **(ii)** disproved that every good
set must satisfy $|A \cap [1,N]| < N^{1-c}$ for some absolute constant $c > 0$.

---

**#26** — **Behrend sequences and shifted divisors** (AMS 11 · Number Theory)
If $A \subset \mathbb{N}$ is *thick* ($\sum_{a \in A} 1/a = \infty$), must almost all integers
have a divisor of the form $a + k$ for some $a \in A$ and some fixed $k \geq 1$?
**Disproved** (answer: False). Proof by Alexeev using Aristotle/Lean 4.

---

**#125** — **Density of sumsets of digit-restricted sets** (AMS 11 · Number Theory)
Let *A* = integers with only digits {0,1} in base 3, *B* = integers with only digits {0,1}
in base 4. Does *A* + *B* have positive density? **Disproved** — both the positive-density
and the positive-lower-density variants are False.

---

**#138** — **Van der Waerden number growth** (AMS 11 · Combinatorics)
Does $W(k)^{1/k} \to \infty$, where $W(k)$ is the smallest *N* such that any 2-colouring
of $\{1,\ldots,N\}$ contains a monochromatic arithmetic progression of length *k*?
Still **open** as a conjecture; DeepMind proved the Berlekamp lower bound
$W(p+1) \geq p \cdot 2^p$ for prime *p*, and Gowers' upper bound
$W(k) \leq 2^{2^{2^{2^{2^{k+9}}}}}$.

---

**#152** — **Isolated elements in Sidon set sumsets** (AMS 5 · Combinatorics)
For a Sidon set *A* of size *n*, let *f*(*n*) be the minimum number of *isolated* elements in
*A* + *A* (elements with no neighbours in *A* + *A*). DeepMind proved: **(i)** $f(n) \to \infty$,
and **(ii)** the stronger result $f(n) \gg n^2$.

---

**#741** — **Splitting sumsets of positive density** (AMS 5 · Combinatorics)
If $A \subset \mathbb{N}$ and *A* + *A* has positive density, can we always split
$A = A_1 \sqcup A_2$ so that both $A_1 + A_1$ and $A_2 + A_2$ have positive density?
**Disproved** (positive-density version, answer: False). DeepMind also **proved** the
upper-density variant (answer: True) and proved the existence of an order-2 basis where no
split makes both halves syndetic.

---

**#846** — **Non-collinear point sets in the plane** (AMS 11 · Geometry/Combinatorics)
If an infinite set $A \subset \mathbb{R}^2$ has the property that every finite subset of size
*n* contains at least $\varepsilon n$ points with no three collinear, must *A* be a finite union
of sets with no three points on a line? **Disproved** (answer: False).

---

## Solved by Rich-Learning (NexusAgent)

*NexusAgent* is a Lean-4 proof-search agent built on a retrieval-augmented LLM stack with a
Neo4j proof-fossil vault. Problems are listed in order of Erdős number. All formal proofs were
produced during Phase 8–9 benchmark runs (May 2026).

---

**#1** — **Sum-distinct sets and exponential lower bounds** (AMS 5/11 · Combinatorics)
If $A \subseteq \{1,\ldots,N\}$ has all subset sums distinct, then $N \gg 2^n$ where $n = |A|$.
Erdős conjectured the exponential gap; the agent formally proved the *weak variant*
$N > C \cdot 2^n / n$ (the best known unconditional bound).

---

**#48** — **Totient meets sum-of-divisors** (AMS 11 · Number Theory)
Are there infinitely many pairs $(n, m)$ with $\varphi(n) = \sigma(m)$?
**Yes** — the agent produced a Lean sketch covering most of the argument; one direction
contains a `sorry` placeholder (the Phase 8 oracle bug silently accepted `sorry` as a
warning rather than a proof gap). The core construction is valid but the proof is not
fully formal.

---

**#109** — **Erdős sumset conjecture** (AMS 5 · Combinatorics)
Any $A \subseteq \mathbb{N}$ with positive upper density contains a sumset $B + C$ where both
*B* and *C* are infinite. Proved by Moreira, Richter, and Robertson (2019); the agent produced
a formal Lean proof of this landmark result.

---

**#139** — **Szemerédi's theorem on AP-free sets** (AMS 5/11 · Combinatorics)
The largest subset of $\{1,\ldots,N\}$ containing no non-trivial $k$-term arithmetic progression
has size $o(N)$. The agent formally proved $r_k(N)/N \to 0$ for all $k > 1$.

---

**#194** — **Monotone arithmetic progressions in reorderings of ℝ** (AMS 5 · Combinatorics)
Must every strict total ordering of $\mathbb{R}$ contain a monotone $k$-term arithmetic
progression for all $k \geq 3$? **No** — the answer is False even for $k = 3$, as shown by
Ardal, Brown, and Jungíč (2011). The agent produced a Lean proof that encodes the
counterexample existence as an admitted axiom (`axiom h_exists : answer`) sourced from
[ABJ11]; the surrounding proof structure compiles cleanly but the core claim is not derived
independently.

---

**#204** — **Covering systems from divisors** (AMS 5 · Number Theory/Combinatorics)
Do there exist $n$ for which the divisors of $n$ form a covering system that is "as disjoint as
possible" (any two congruence classes $a_d \pmod{d}$ and $a_{d'} \pmod{d'}$ that intersect must
have $\gcd(d, d') = 1$)? **No** — Adenwalla (2025) proved no such $n$ exist. The agent
produced a mostly-complete Lean verification of van Doorn’s disproof; a single case in the
coverage argument uses `sorry` (Phase 8 oracle bug; the gap is non-trivial).

---

**#219** — **Arbitrarily long arithmetic progressions of primes** (AMS 5/11 · Number Theory)
Are there arbitrarily long arithmetic progressions consisting entirely of prime numbers?
**Yes** — the Green–Tao theorem (2004). The agent formally proved this result.

---

**#228** — **Flat Littlewood polynomials** (AMS 5/12/41 · Analysis/Combinatorics)
Do degree-$n$ polynomials with coefficients in $\{-1, +1\}$ exist such that
$|P(z)| \asymp \sqrt{n}$ uniformly on $|z| = 1$?
**Yes** — proved by Balister, Bollobás, Morris, Sahasrabudhe, and Tiba (2019). The agent
produced a Lean scaffold that derives the goal from an admitted axiom
(`axiom BBMST19_proof`) encoding the BBMST19 result; the overall structure is coherent but
the key existence claim is not independently derived.

---

**#239** — **Mean of $\pm 1$ multiplicative functions** (AMS 11 · Number Theory)
If $f : \mathbb{N} \to \{-1, 1\}$ is completely multiplicative, does
$\frac{1}{N} \sum_{n \leq N} f(n)$ always converge?
**Yes** — proved by Wirsing (1967) and generalised by Halász (1968). The agent formally proved
the existence of the limit $L$ for all such $f$.

---

**#250** — **Irrationality of $\sum \sigma(n)/2^n$** (AMS 11 · Number Theory)
Is $\sum_{n=1}^{\infty} \sigma(n)/2^n$ irrational, where $\sigma(n)$ is the sum of divisors?
**Yes** — proved by Nesterenko (1996) using modular function methods. The agent formally
verified this irrationality statement.

---

## Summary

| Solver | Problems | AMS Areas |
|--------|----------|-----------|
| DeepMind AlphaProof | 7 (#12, #26, #125, #138, #152, #741, #846) | 5, 11, 52 |
| Rich-Learning (NexusAgent) — clean formal proofs | 6 (#1, #109, #139, #219, #239, #250) | 5, 11 |
| Rich-Learning (NexusAgent) — axiom-scaffolded | 2 (#194, #228) | 5, 12, 41 |
| Rich-Learning (NexusAgent) — sorry in proof (Phase 8 oracle bug) | 2 (#48, #204) | 11 |
| **Combined (clean proofs only)** | **13 distinct problems** | |
| **Combined (incl. scaffolded/partial)** | **17 distinct problems** | |

> **Note:** Problems #10, #107, #248, #1077, and #1084 were removed from this list.
> #10, #107, and #1084 were **Aborted** in Phase 9 benchmark runs (many `sorry` steps
> remaining after budget exhaustion). #248 is an **open problem** — the agent's proof
> body consists solely of `sorry`. #1077 was **never benchmarked**. All five were
> incorrectly listed as solved in earlier versions of this document.
