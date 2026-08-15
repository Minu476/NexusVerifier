#!/usr/bin/env python3
"""Paired analysis for the transition-dump A/B (pre-registered design:
data/results/TRANSITION_DUMP_AB_DESIGN_2026-08-14.md).

Reads the two bench JSONs, forms per-problem paired binary outcomes
(independent solve = outcome Solved, citation-audited at solve time by the
unconditional gate), reports discordant pairs and exact McNemar.
Directional by pre-commitment: n=31 cannot detect small effects.
"""
import json, sys
from math import comb

def load(path):
    with open(path) as f:
        return json.load(f)

def key(r):
    return r["Id"] if "Id" in r else r.get("ProblemId") or r.get("id")

def outcomes(doc):
    out = {}
    for r in doc.get("Results", doc.get("results", [])):
        out[key(r)] = (r.get("Outcome") or r.get("outcome")) == "Solved"
    return out

c_path, t_path = sys.argv[1], sys.argv[2]
c, t = outcomes(load(c_path)), outcomes(load(t_path))
problems = sorted(set(c) & set(t))
only_c, only_t = set(c) - set(t), set(t) - set(c)

b = sum(1 for p in problems if t[p] and not c[p])   # treatment-only solves
cdis = sum(1 for p in problems if c[p] and not t[p])  # control-only solves
n = len(problems)
sc, st = sum(c.values()), sum(t.values())

# exact McNemar two-sided: binomial on discordant pairs
d = b + cdis
p = 1.0
if d > 0:
    k = min(b, cdis)
    p = min(1.0, 2 * sum(comb(d, i) for i in range(0, k + 1)) / 2**d)

print(f"paired problems : {n}  (control-only ids: {len(only_c)}, treatment-only: {len(only_t)})")
print(f"control solves  : {sc}/{n}")
print(f"treatment solves: {st}/{n}")
print(f"discordant      : b (treatment gains) = {b}, c (treatment losses) = {cdis}")
print(f"exact McNemar p = {p:.4f}")
print(f"MDE reminder    : significance needs ~b>=6,c=0 (19/31) or b>=9..10 with 1-2 losses")
for p in sorted(only_c | only_t):
    print(f"  unpaired: {p}")
