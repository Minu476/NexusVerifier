#!/usr/bin/env python3
"""Paired analysis for the transition-dump A/B (pre-registered design:
data/results/TRANSITION_DUMP_AB_DESIGN_2026-08-14.md).

Bench JSONs are lists of rows {Id, Result: {Outcome: int}} with Outcome 0 = Solved
(independent — the solve-time citation gate is unconditional). Ids carry the arm
tag prefix; pairing strips it.
"""
import json, sys
from math import comb

def outcomes(path, tag):
    return {r["Id"][len(tag):]: r["Result"]["Outcome"] == 0 for r in json.load(open(path))}

c_path, t_path = sys.argv[1], sys.argv[2]
c, t = outcomes(c_path, "TDABC1_"), outcomes(t_path, "TDABT1_")
problems = sorted(set(c) & set(t))
only_c, only_t = sorted(set(c) - set(t)), sorted(set(t) - set(c))

b = sum(1 for p in problems if t[p] and not c[p])
cdis = sum(1 for p in problems if c[p] and not t[p])
d = b + cdis
p = min(1.0, 2 * sum(comb(d, i) for i in range(min(b, cdis) + 1)) / 2**d) if d else 1.0

sc, st = sum(c[q] for q in problems), sum(t[q] for q in problems)
print(f"paired problems : {len(problems)}  (control-only: {only_c}  treatment-only: {only_t})")
print(f"control solves  : {sc}/{len(problems)}")
print(f"treatment solves: {st}/{len(problems)}")
print(f"discordant      : b (treatment gains) = {b}, c (treatment losses) = {cdis}")
print(f"exact McNemar p = {p:.4f}")
print("gains  :", [q for q in problems if t[q] and not c[q]])
print("losses :", [q for q in problems if c[q] and not t[q]])
