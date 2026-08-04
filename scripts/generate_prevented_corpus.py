#!/usr/bin/env python3
"""
generate_prevented_corpus.py — close the citation hole at its source.

## The problem this solves

FC100 stub problems import `FormalConjectures.Subsets.FC100SolvedSet1` to get the
type/definitions their statement needs. That index module transitively imports the module
containing the ORIGINAL, complete proof of the very theorem being asked for — so a stub can be
"solved" with `exact <original_declaration>`. That compiles, is axiom-clean, and preserves the
declaration signature, so no downstream check catches it as malformed. It simply isn't proof
search.

Measured impact (data/results/PIVOT_AB_2026-08-03.md): 100% of both A/B arms' raw "solves" were
this exploit. Round 2 added detection and keep-searching, and citations *still* dominated
(5-8 per run) — see PIVOT_AB2_NULL_RESULT_2026-08-04.md. Detection changes what we COUNT;
this changes what the model CAN DO.

## What it does

For each stub, generate a "prevent" module: a copy of the module that defines the target
theorem, with the target declaration DELETED, and rewrite the stub to import that copy instead
of FC100SolvedSet1. The definitions the statement needs survive; the answer key does not exist.

A citation then fails with a hard `Unknown identifier` compile error rather than silently
scoring nothing — the model gets immediate, actionable feedback in the same turn loop instead
of burning episodes on a move that looks like it worked.

Verified end-to-end on tripleProduct_const before this script was written:
    exact tripleProduct_const a       -> error: Unknown identifier `tripleProduct_const`
    unfold tripleProduct; funext x; simp  -> compiles clean

## Strategy and its fallback

DELETE is the default: the name is gone, so citation is impossible. Deleting a `@[simp]` lemma
can in principle break later proofs in the same module, so every generated module is built and
any failure is reported rather than silently shipped. `--strategy sorry` replaces the proof body
with `sorry` instead: the name still resolves, but citing it makes the proof sorry-backed, which
LeanOracle's existing `#print axioms` check already rejects. Weaker (no compile error, so no
sharp feedback) but survives cases where deletion breaks a module.

## Usage

    python3 scripts/generate_prevented_corpus.py \
        --stub-dir data/swarm_pivot_ab \
        --out-dir  data/swarm_pivot_ab_prevented

    # then build + verify (does the real work of proving this worked):
    python3 scripts/generate_prevented_corpus.py --verify-only \
        --stub-dir data/swarm_pivot_ab --out-dir data/swarm_pivot_ab_prevented

Verification compiles, per problem, a citation probe (must FAIL with unknown identifier) and the
generated stub (must compile with only its own `sorry`). A generated corpus that has not passed
verification should not be used for an experiment.
"""
from __future__ import annotations

import argparse
import re
import shutil
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
FC = REPO / "formal-conjectures"
PREVENT_DIR = FC / "FormalConjectures" / "Prevent"
FC100_IMPORT = "import FormalConjectures.Subsets.FC100SolvedSet1"

# Lines that begin a new top-level item — used to find where a declaration ends.
DECL_START = re.compile(
    r"^(@\[|/--|/-!|theorem\s|lemma\s|def\s|abbrev\s|instance\s|structure\s|inductive\s|"
    r"class\s|namespace\s|end\b|open\s|section\b|variable\s|noncomputable\s|private\s|"
    r"protected\s|@\[simp)"
)


def bare_name(qualified: str) -> str:
    """Last dot-separated segment, treating «...» as one atomic component.

    Lean escapes identifiers containing non-identifier characters in guillemets, e.g.
    `Arxiv.«1609.08688».tripleProduct_const` — the dot inside «1609.08688» is NOT a namespace
    separator, so a naive rsplit('.') returns "08688" and every downstream lookup fails.
    """
    depth, last = 0, -1
    for i, c in enumerate(qualified):
        if c == "«":
            depth = 1
        elif c == "»":
            depth = 0
        elif c == "." and not depth:
            last = i
    return qualified[last + 1:]


def parse_stub(path: Path) -> tuple[str, str] | None:
    """(qualified_original, bare) from the stub's `Stripped stub: X → ...` header."""
    m = re.search(r"Stripped stub:\s*([^\s→]+)\s*→", path.read_text())
    if not m:
        return None
    q = m.group(1).strip()
    return q, bare_name(q)


def find_defining_module(qualified: str, bare: str) -> Path | None:
    """The .lean file declaring this problem's own `theorem <bare>`, disambiguated by namespace.

    Matching on the bare name alone is WRONG and silently patches an unrelated problem: names
    like `a_6` recur across dozens of OEIS files (`OEIS/80170.lean`, `OEIS/67720.lean`,
    `OEIS/303656.lean`, ...). The first version of this function took the first bare-name hit and
    generated a prevent module from the wrong source; the stub then failed to compile because the
    definitions it needed lived in a different file. Require the enclosing namespace from the
    stub's qualified name to match too.
    """
    ns = qualified[: len(qualified) - len(bare) - 1] if len(qualified) > len(bare) else ""
    decl = re.compile(rf"^\s*(theorem|lemma)\s+{re.escape(bare)}\b", re.M)
    candidates = []
    for p in (FC / "FormalConjectures").rglob("*.lean"):
        if "Subsets/FC100" in str(p) or "/Prevent/" in str(p):
            continue
        try:
            text = p.read_text()
        except (UnicodeDecodeError, OSError):
            continue
        if not decl.search(text):
            continue
        if ns and re.search(rf"^namespace\s+{re.escape(ns)}\s*$", text, re.M):
            return p  # exact namespace match wins outright
        candidates.append(p)
    # No namespace match: only safe if the bare name is globally unique.
    return candidates[0] if len(candidates) == 1 else None


def locate_declaration(lines: list[str], bare: str) -> tuple[int, int] | None:
    """0-indexed [start, end) span of the declaration, including its attributes and docstring.

    Walks back over immediately-preceding attribute/docstring lines so the deletion doesn't
    strand an orphan `@[simp, ...]` line, and forward to the next top-level item.
    """
    decl = re.compile(rf"^\s*(theorem|lemma)\s+{re.escape(bare)}\b")
    idx = next((i for i, l in enumerate(lines) if decl.match(l)), None)
    if idx is None:
        return None

    start = idx
    while start > 0:
        prev = lines[start - 1].strip()
        if prev.startswith("@["):
            start -= 1
        elif prev.endswith("-/"):
            # Docstring. May be MULTI-LINE, in which case the immediately preceding line ends
            # with `-/` but does not contain `/--` — walk back to the opening `/--` or the
            # deletion strands an orphan docstring that then binds to the NEXT declaration
            # (observed: P_f_undefined_at_2 -> "unexpected token '/--'; expected 'lemma'").
            j = start - 1
            while j > 0 and not lines[j].strip().startswith(("/--", "/-!")):
                j -= 1
            if lines[j].strip().startswith("/--"):
                start = j
            else:
                break
        else:
            break

    end = idx + 1
    while end < len(lines):
        if lines[end].strip() and DECL_START.match(lines[end]):
            break
        end += 1
    return start, end


def module_name_for(bare: str) -> str:
    safe = re.sub(r"[^A-Za-z0-9_]", "_", bare)
    return f"P_{safe}"


def write_prevent_module(mod: Path, bare: str, strategy: str) -> int:
    """Write the prevent copy of `mod` with `bare` deleted or sorry-ed. Returns lines affected."""
    lines = mod.read_text().split("\n")
    span = locate_declaration(lines, bare)
    if not span:
        raise ValueError(f"declaration span not located for {bare}")
    s, e = span

    if strategy == "delete":
        kept = lines[:s] + lines[e:]
    else:
        block = "\n".join(lines[s:e])
        # Replace the proof, keep the signature. `:= by ...` and bare `:= term` both occur.
        new_block, n = re.subn(r":=\s*by\b.*", ":= by\n  sorry", block, count=1, flags=re.S)
        if not n:
            new_block, n = re.subn(r":=(?!.*:=).*", ":= by\n  sorry", block, count=1, flags=re.S)
        if not n:
            raise ValueError(f"could not locate proof body for {bare}")
        kept = lines[:s] + new_block.split("\n") + lines[e:]

    (PREVENT_DIR / f"{module_name_for(bare)}.lean").write_text("\n".join(kept))
    return e - s


def build_all() -> tuple[bool, str]:
    import os
    p = subprocess.run(["lake", "build", "FormalConjectures"], cwd=FC,
                       capture_output=True, text=True,
                       env={**os.environ, "PATH": f"{Path.home()}/.elan/bin:" + os.environ.get("PATH", "")})
    return p.returncode == 0, p.stdout + p.stderr


def failed_modules(build_output: str) -> set[str]:
    """Prevent-module basenames that lake reported as failing."""
    return set(re.findall(r"FormalConjectures\.Prevent\.(P_[A-Za-z0-9_]+)", build_output))


def generate(stub_dir: Path, out_dir: Path, strategy: str) -> list[dict]:
    PREVENT_DIR.mkdir(parents=True, exist_ok=True)
    out_dir.mkdir(parents=True, exist_ok=True)
    results = []

    for stub in sorted(stub_dir.glob("*.lean")):
        rec: dict = {"stub": stub.name}
        parsed = parse_stub(stub)
        if not parsed:
            rec["status"] = "SKIP: no 'Stripped stub' header"
            results.append(rec)
            continue
        qualified, bare = parsed
        rec.update(original=qualified, bare=bare)

        mod = find_defining_module(qualified, bare)
        if not mod:
            rec["status"] = "FAIL: defining module not found"
            results.append(rec)
            continue
        rec["defining_module"] = str(mod.relative_to(FC))

        try:
            removed = write_prevent_module(mod, bare, strategy)
        except ValueError as ex:
            rec["status"] = f"FAIL: {ex}"
            results.append(rec)
            continue
        pmod = module_name_for(bare)
        rec["defining_path"] = str(mod)

        # Rewrite only the index import — everything else about the stub is preserved, so the
        # generated corpus differs from the original in exactly one line.
        text = stub.read_text()
        if FC100_IMPORT not in text:
            rec["status"] = "FAIL: stub does not import FC100SolvedSet1"
            results.append(rec)
            continue
        text = text.replace(FC100_IMPORT, f"import FormalConjectures.Prevent.{pmod}")
        (out_dir / stub.name).write_text(text)

        rec.update(status="generated", prevent_module=pmod, removed_lines=removed,
                   strategy=strategy)
        results.append(rec)

    return results


def apply_fallback(rows: list[dict]) -> int:
    """Build; any prevent module that fails under `delete` is regenerated with `sorry`.

    Deletion is preferred (the name is gone, so a citation is a hard compile error and the model
    gets sharp feedback), but it is not always possible: a module may genuinely reference the
    target internally — e.g. FormalConjectures/.../AME.lean proves `ame_3_2_exists` via
    `simpa using ame_3_exists`, so removing `ame_3_exists` breaks its own module. For those,
    `sorry` keeps the name resolvable but makes any proof citing it sorry-backed, which
    LeanOracle's existing `#print axioms` check already rejects. Weaker, but correct.

    Returns the number switched to the fallback.
    """
    ok, out = build_all()
    if ok:
        return 0

    failing = failed_modules(out)
    switched = 0
    for r in rows:
        if r.get("status") != "generated":
            continue
        if r.get("prevent_module") not in failing:
            continue
        try:
            r["removed_lines"] = write_prevent_module(Path(r["defining_path"]), r["bare"], "sorry")
            r["strategy"] = "sorry (fallback: delete broke the defining module)"
            switched += 1
        except ValueError as ex:
            r["status"] = f"FAIL: fallback failed: {ex}"
    return switched


def run_lean(rel_path: str) -> tuple[int, str]:
    p = subprocess.run(
        ["lake", "env", "lean", rel_path],
        cwd=FC, capture_output=True, text=True,
        env={**__import__("os").environ,
             "PATH": f"{Path.home()}/.elan/bin:" + __import__("os").environ.get("PATH", "")},
    )
    return p.returncode, p.stdout + p.stderr


def verify(stub_dir: Path, out_dir: Path) -> int:
    """Build every prevent module, then prove per problem that citation fails and the stub compiles."""
    import os
    print("=== building all prevent modules ===")
    b = subprocess.run(["lake", "build", "FormalConjectures"], cwd=FC,
                       capture_output=True, text=True,
                       env={**os.environ, "PATH": f"{Path.home()}/.elan/bin:" + os.environ.get("PATH", "")})
    if b.returncode != 0:
        print("BUILD FAILED:\n" + (b.stdout + b.stderr)[-3000:])
        return 1
    print("build ok\n")

    tmp = FC / "_nexus_tmp"
    tmp.mkdir(exist_ok=True)
    ok = fail = 0

    for stub in sorted(out_dir.glob("*.lean")):
        parsed = parse_stub(stub)
        if not parsed:
            continue
        _, bare = parsed
        text = stub.read_text()
        # Everything above the stub's own `theorem` line: imports, opens, namespace. Probes are
        # appended after it so they see exactly the environment the model sees.
        header = text.split("\ntheorem ")[0]

        # Probe A — NAME RESOLUTION. `#check @name` tests only whether the identifier exists,
        # independent of its arity or type. An earlier version substituted `exact <name>` into
        # the proof, which produced spurious "Type mismatch" failures whenever the theorem took
        # arguments — a defect in the probe, not in the prevention.
        pa = tmp / f"vrfy_res_{bare}.lean"
        pa.write_text(f"{header}\n\n#check @{bare}\n")
        _, out_res = run_lean(f"_nexus_tmp/{pa.name}")
        resolves = "nknown identifier" not in out_res

        # Probe B — for the `sorry` fallback the name intentionally still resolves, so the
        # property to check is that anything citing it is sorry-backed, which LeanOracle's
        # existing `#print axioms` check rejects.
        pb = tmp / f"vrfy_ax_{bare}.lean"
        pb.write_text(f"{header}\n\n#print axioms {bare}\n")
        _, out_ax = run_lean(f"_nexus_tmp/{pb.name}")
        sorry_backed = "sorryAx" in out_ax

        # Probe C — the generated stub must still compile (its statement's definitions survived).
        pc = tmp / f"vrfy_stub_{bare}.lean"
        pc.write_text(text)
        _, out_stub = run_lean(f"_nexus_tmp/{pc.name}")
        stub_compiles = not re.search(r"error(\(|:)", out_stub)

        # Citation is neutralised either way: the name is gone, or it is poisoned.
        blocked = (not resolves) or sorry_backed
        mode = "deleted" if not resolves else ("sorry-backed" if sorry_backed else "REACHABLE")
        good = blocked and stub_compiles
        ok, fail = (ok + 1, fail) if good else (ok, fail + 1)
        print(f"  {'PASS' if good else 'FAIL'}  {bare:<42} citation={mode:<13} stub_compiles={stub_compiles}")
        if not good:
            for l in [l for l in out_stub.split("\n") if "error" in l.lower()][:2]:
                print(f"          stub: {l.strip()[:150]}")
            if not blocked:
                print(f"          citation still reachable and not sorry-backed")
        for f_ in (pa, pb, pc):
            f_.unlink(missing_ok=True)

    print(f"\n=== {ok} passed, {fail} failed ===")
    return 0 if fail == 0 else 1


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--stub-dir", required=True, type=Path)
    ap.add_argument("--out-dir", required=True, type=Path)
    ap.add_argument("--strategy", choices=["delete", "sorry"], default="delete")
    ap.add_argument("--verify-only", action="store_true")
    ap.add_argument("--clean", action="store_true", help="remove generated prevent modules and out-dir first")
    a = ap.parse_args()

    if a.clean:
        shutil.rmtree(PREVENT_DIR, ignore_errors=True)
        shutil.rmtree(a.out_dir, ignore_errors=True)
        print("cleaned generated artifacts")

    if not a.verify_only:
        rows = generate(a.stub_dir, a.out_dir, a.strategy)
        bad = [r for r in rows if not r["status"].startswith(("generated", "SKIP"))]
        if bad:
            for r in bad:
                print(f"  {r['status']:<50} {r.get('bare','?')}")
            return 1

        if a.strategy == "delete":
            print("building to find modules where deletion breaks the source...")
            n = apply_fallback(rows)
            if n:
                print(f"  switched {n} module(s) to the `sorry` fallback")

        for r in rows:
            if r["status"] == "generated":
                print(f"  {r['strategy']:<48} {r['bare']}")
        print(f"\ngenerated {sum(1 for r in rows if r['status']=='generated')}")

    return verify(a.stub_dir, a.out_dir)


if __name__ == "__main__":
    sys.exit(main())
