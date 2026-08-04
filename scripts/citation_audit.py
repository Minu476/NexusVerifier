#!/usr/bin/env python3
"""
Citation audit for NexusVerifier solve results.

After a swarm run, each "solved" stub has a winning proof (the tactic block that
dropped sorry to 0). Because the stub imports FC100SolvedSet1 to get the type
definitions, the ORIGINAL complete proof is also in scope — so the prover can
"solve" a fresh stub by citing the original (e.g. `exact example_maximal_sidon`).

This is documented in docs/FC100_GAP_SET_METHODOLOGY.md as the mechanism behind
the inflated "28% solve rate" that was really answer-key citation. The existing
HasSorryAxiomAsync check does NOT catch it (the original isn't sorry-backed).

This audit reads each solved stub's winning proof and classifies it:

  - CITATION  : the proof names the original declaration → not independent
  - INDEPENDENT : the proof does not name the original → real proof search
  - UNKNOWN   : no proof text recorded / couldn't classify

Usage:
  # From a bench results JSON (the fossilized tactic block per problem):
  python3 citation_audit.py <results.json> [--stub-dir data/swarm_solvable]

  # Or query Neo4j directly for the winning fossil's tacticBlock:
  python3 citation_audit.py --neo4j <host:port> --user <u> --password <p>

Classification rule: a proof is a CITATION if any line, after stripping comments
and whitespace, is exactly `exact <bare_orig_name>` or a trivial wrapper around
it (intro/simpa/use wrapping a single `exact <orig>`). Otherwise INDEPENDENT.
"""
import argparse
import json
import re
import sys
from pathlib import Path


def _find_first_top_level_assign(text: str) -> int:
    """Index of the first ':=' that isn't nested inside (), [], or {} — i.e. the
    theorem signature's own proof-start assignment, not a named-argument binding
    like '(α := α)' or a 'let x := ...' inside the tactic block."""
    depth = 0
    i, n = 0, len(text)
    while i < n - 1:
        c = text[i]
        if c in '([{':
            depth += 1
        elif c in ')]}':
            depth = max(0, depth - 1)
        elif c == ':' and text[i + 1] == '=' and depth == 0:
            return i
        i += 1
    return -1


def extract_proof_body(full_sketch: str) -> str:
    """
    Isolate the target theorem's proof term/tactic block from a full Lean source
    file (imports + namespace + theorem signature + proof + `end`).

    classify_proof's "≤3 distinct tactic verbs ⇒ citation" heuristic assumes it is
    given ONLY the proof body. NexusVerifier's bench JSON only records the whole
    FinalSketch (the entire compiled file), so passing that straight to
    classify_proof inflates the verb count with "import", "namespace", "theorem",
    "end", etc. and the citation check never fires. Take the FIRST top-level
    (paren-depth 0) ':=' — that's the theorem signature's own proof-start
    assignment; a naive last-':=' search instead lands inside named-argument
    syntax like '(α := α)' in the winning tactic and truncates the real proof.
    Cut off at the next 'end' line, so only the actual proof is classified.
    """
    if not full_sketch:
        return full_sketch
    cleaned = re.sub(r'/-.*?-/', '', full_sketch, flags=re.DOTALL)
    cleaned = re.sub(r'--.*$', '', cleaned, flags=re.MULTILINE)
    idx = _find_first_top_level_assign(cleaned)
    if idx == -1:
        return cleaned.strip()
    body = cleaned[idx + 2:]
    body = re.split(r'\n\s*end\b', body)[0]
    return body.strip()


def classify_proof(proof_text: str, original_bare_name: str) -> str:
    """
    Classify a winning tactic block against the original declaration's bare name.

    Returns 'CITATION' | 'INDEPENDENT' | 'UNKNOWN'.
    """
    if not proof_text or not proof_text.strip():
        return "UNKNOWN"

    # Strip Lean comments (-- line and /- block -/)
    cleaned = re.sub(r'/-.*?-/', '', proof_text, flags=re.DOTALL)
    cleaned = re.sub(r'--.*$', '', cleaned, flags=re.MULTILINE)

    # Normalize: collect non-empty, stripped lines (the actual tactic invocations)
    lines = [ln.strip() for ln in cleaned.split('\n') if ln.strip()]
    if not lines:
        return "UNKNOWN"

    # Join into a single token stream for keyword detection
    blob = ' '.join(lines)

    # Does the proof mention the original declaration name as an identifier?
    # Use word boundaries so 'exact a_0' matches but 'exact a_05' doesn't.
    # Also catch dot-qualified forms like 'exact Erdos42.example_maximal_sidon'.
    names_to_check = {original_bare_name}
    # also catch the bare name as a substring of a qualified ref
    mentions_orig = bool(re.search(r'\b' + re.escape(original_bare_name) + r'\b', blob))

    if not mentions_orig:
        return "INDEPENDENT"

    # The original name IS mentioned. Is the proof ENTIRELY a citation?
    # A pure citation looks like one of:
    #   exact <orig>
    #   exact @<orig>
    #   exact <ns>.<orig>
    #   exact ⟨<orig>, ...⟩   (still a citation if the only non-trivial term is orig)
    #   simpa using <orig>    (trivial wrapper)
    #   trivial / decide / rfl / norm_num  → INDEPENDENT even if orig mentioned in a comment
    trivial_only_patterns = [
        r'^rfl\b', r'^trivial\b', r'^decide\b', r'^norm_num\b',
        r'^simp\b', r'^unfold\b', r'^intro\b.*\brfl\b',
    ]
    # If the whole proof is a trivial tactic (not naming orig), it's independent.
    for pat in trivial_only_patterns:
        if re.match(pat, blob) and not mentions_orig:
            return "INDEPENDENT"

    # If every non-trivial line references the original, it's a citation.
    # Heuristic: the proof mentions orig AND has ≤ 3 distinct tactic verbs.
    tactic_verbs = set()
    for ln in lines:
        first_word = ln.split()[0] if ln.split() else ''
        if first_word:
            tactic_verbs.add(first_word)

    if mentions_orig and len(tactic_verbs) <= 3:
        return "CITATION"

    # Mentioned orig but in a longer, varied proof — likely a real derivation
    # that happens to reference orig among other things. Mark INDEPENDENT but
    # flag for manual review.
    return "INDEPENDENT"


def audit_results_json(results_path: str, stub_dir: str = "data/swarm_solvable") -> dict:
    """Audit a bench-*.json results file."""
    results = json.loads(Path(results_path).read_text())

    # Build bare-name → original-decl-name map from stub filenames + manifest
    manifest_path = Path(stub_dir) / "manifest.json"
    stub_to_orig = {}
    if manifest_path.exists():
        for m in json.loads(manifest_path.read_text()):
            bare = m.get("bare", "")
            orig = m.get("decl", bare).split(".")[-1]
            stub_to_orig[bare] = orig

    summary = {"CITATION": [], "INDEPENDENT": [], "UNKNOWN": [], "total": 0}
    for rec in results:
        # BenchRecord shape: {Id, Domain, Statement, Result: {...}}
        r = rec.get("Result", rec)
        # ProofOutcome is serialized by System.Text.Json as its numeric value (no
        # JsonStringEnumConverter registered), not the enum name — so "Outcome" is
        # an int here (0 == ProofOutcome.Solved; see NexusAgent.Core/Models/ProofResult.cs).
        # Accept both the numeric and string forms so this also works if serialization
        # is ever changed to emit enum names.
        outcome = r.get("Outcome")
        if outcome != 0 and outcome != "Solved":
            continue
        summary["total"] += 1
        # The winning proof text — NexusVerifier records it in the fossil, but the
        # bench JSON may carry FinalSketch or a Tier075Telemetry field. Try common keys.
        proof = (r.get("FinalSketch") or r.get("WinningProof")
                 or r.get("ProofText") or "")
        problem_id = rec.get("Id", r.get("ProblemId", "?"))
        # Extract the bare stub name from the problem id. The id is built as
        # "{source}_{filename}" (see NexusAgent.Cli/Program.cs RunBenchAsync), and
        # both the source tag AND the filename itself commonly contain underscores
        # (e.g. "SwarmSolv_eqSystem4_has_solution_d2", "PivotAB-Treatment_ame_2_exists"),
        # so naively splitting on "_" and taking the last token silently truncates
        # multi-underscore bare names to their final segment (e.g. "exists" instead
        # of "ame_2_exists") and the citation check below then never matches.
        # Instead, match against the known bare names from manifest.json — pick the
        # longest bare name that the id ends with, since bare names never contain
        # the "_" + filename boundary ambiguity when compared this way.
        bare_guess = ""
        if problem_id != "?" and stub_to_orig:
            for candidate in sorted(stub_to_orig, key=len, reverse=True):
                if problem_id == candidate or problem_id.endswith("_" + candidate):
                    bare_guess = candidate
                    break
        if not bare_guess:
            # Fallback for when manifest.json is missing/doesn't have this stub.
            bare_guess = problem_id.split("_")[-1] if problem_id != "?" else ""
        orig_name = stub_to_orig.get(bare_guess, bare_guess)
        proof_body = extract_proof_body(proof)
        verdict = classify_proof(proof_body, orig_name)
        summary[verdict].append({"id": problem_id, "orig": orig_name,
                                  "proof_preview": proof_body[:120]})

    return summary


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("results_json", nargs="?", help="bench-*.json results file")
    ap.add_argument("--stub-dir", default="data/swarm_solvable")
    args = ap.parse_args()

    if not args.results_json:
        ap.error("results_json required (or use --neo4j)")

    summary = audit_results_json(args.results_json, args.stub_dir)

    print(f"=== Citation Audit: {args.results_json} ===")
    print(f"Total solves audited: {summary['total']}")
    print(f"  INDEPENDENT (real proof search): {len(summary['INDEPENDENT'])}")
    print(f"  CITATION (answer-key leak):      {len(summary['CITATION'])}")
    print(f"  UNKNOWN (no proof text):         {len(summary['UNKNOWN'])}")
    if summary["CITATION"]:
        print("\nCitation solves (not independent):")
        for c in summary["CITATION"]:
            print(f"  ✗ {c['id']}: cites {c['orig']}")
    if summary["INDEPENDENT"]:
        print("\nIndependent solves (the real number):")
        for c in summary["INDEPENDENT"]:
            print(f"  ✓ {c['id']}")


if __name__ == "__main__":
    main()
