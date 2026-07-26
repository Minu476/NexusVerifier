using System.Text.RegularExpressions;

namespace NexusAgent.Core.Agent;

/// <summary>
/// Structural validity checks that guard against reward hacking.
/// A "structurally valid" sketch preserves every theorem and lemma declaration
/// from the original — exact name, binders, and type — and introduces no new
/// value-level declaration. Only the proof term (after ':=' or 'by') may change.
///
/// Used by both <see cref="NexusProverSubagent"/> (per-turn gate) and
/// <see cref="NexusOrchestrator"/> (per-episode bestSketch gate).
/// </summary>
internal static class SketchValidator
{
    /// <summary>
    /// Returns true if <paramref name="candidateSketch"/> preserves every theorem
    /// and lemma declaration from <paramref name="originalSketch"/> — same name,
    /// binders, and type, verbatim modulo whitespace — and introduces no new
    /// def/abbrev/instance/axiom/structure/inductive/class declaration.
    ///
    /// A fully-proved or partially-improved sketch that fails this check has either
    /// renamed/restated the original declaration(s), or shadowed an identifier the
    /// goal depends on with a new value-level declaration (reward hacking), and must
    /// not be accepted as progress or fossilized.
    ///
    /// Two confirmed exploits this closes (found 2026-07-25 against real FC100 gaps):
    ///   1. Renaming just enough to slip past a substring check, e.g. declaring
    ///      `theorem oeis_a_two_new` when the original was `a_two_new` — the old
    ///      check was `candidateSketch.Contains(originalName)`, which that satisfies.
    ///   2. Keeping the theorem's name AND type text identical while shadowing a
    ///      `def` the type depends on with a fake, trivially-true redefinition
    ///      (e.g. redeclaring `OeisA228828.a` as `fun n => 3*n+1` right before a
    ///      theorem stating `OeisA228828.a 2 = 7`, then `native_decide`). The type
    ///      text never changes, so a name/type check alone can't catch this — only
    ///      banning new top-level value declarations does.
    ///
    /// Vacuously true when the original has no theorem/lemma declarations.
    /// </summary>
    internal static bool IsStructurallyValid(string originalSketch, string candidateSketch)
    {
        var originalDecls = TheoremDeclarationsIn(originalSketch);
        if (originalDecls.Count == 0) return true;

        var candidateByName = TheoremDeclarationsIn(candidateSketch)
            .GroupBy(d => d.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var original in originalDecls)
        {
            // Exact-name lookup, not `candidateSketch.Contains(original.Name)` — a
            // substring match lets `oeis_a_two_new` stand in for `a_two_new`.
            if (!candidateByName.TryGetValue(original.Name, out var match))
                return false;

            // Binders + type must be verbatim (modulo whitespace) — only the proof
            // term after ':=' may differ.
            if (NormalizeWhitespace(match.Signature) != NormalizeWhitespace(original.Signature))
                return false;
        }

        // No new value-level declaration may be introduced. This is the shadowing
        // vector: a candidate can keep a theorem's name and type text byte-identical
        // while redefining a `def`/`abbrev`/etc. the type's identifiers resolve to,
        // silently changing what the (unchanged-looking) theorem actually asserts.
        var originalAux = AuxiliaryDeclarationNamesIn(originalSketch);
        var candidateAux = AuxiliaryDeclarationNamesIn(candidateSketch);
        if (!candidateAux.IsSubsetOf(originalAux))
            return false;

        return true;
    }

    /// <summary>
    /// Extracts distinct theorem/lemma names from <paramref name="sketch"/>, for
    /// callers that only need names (e.g. logging/telemetry). Prefer
    /// <see cref="TheoremDeclarationsIn"/> for validation — this discards the
    /// signature information <see cref="IsStructurallyValid"/> needs.
    /// </summary>
    internal static IReadOnlyList<string> TheoremNamesIn(string sketch) =>
        TheoremDeclarationsIn(sketch).Select(d => d.Name).Distinct().ToList();

    /// <summary>
    /// Like <see cref="TheoremNamesIn"/>, but prefixes each name with any
    /// enclosing <c>namespace</c> blocks. Needed by <see cref="Oracle.LeanOracle"/>'s
    /// <c>#print axioms</c> probe: that probe is appended after the full sketch
    /// text, so a bare name declared inside <c>namespace Foo ... end Foo</c> would
    /// no longer resolve at that point — Lean reports "unknown constant" (no
    /// <c>sorryAx</c> anywhere in the output) instead of the axiom closure,
    /// silently bypassing the sorry-citation-laundering check for exactly the
    /// namespace-wrapped sketches that make up the majority of the FC corpus.
    /// <c>section</c> blocks consume an <c>end</c> like <c>namespace</c> does but
    /// contribute no name prefix; tracked with a two-field stack entry so nesting
    /// stays correctly balanced. Line-anchored regex scan, not a full parser —
    /// consistent with the rest of this file — so it can be fooled by
    /// <c>namespace</c>/<c>end</c> text inside a string or block comment, which
    /// doesn't occur in FC-corpus-derived sketches in practice.
    /// </summary>
    internal static IReadOnlyList<string> QualifiedTheoremNamesIn(string sketch)
    {
        var events = new List<(int Offset, int Kind, string? Value)>();
        // Kind: 0 = namespace open, 1 = section open, 2 = end, 3 = theorem decl
        foreach (Match m in NamespaceOpenRegex.Matches(sketch))
            events.Add((m.Index, 0, m.Groups[1].Value));
        foreach (Match m in SectionOpenRegex.Matches(sketch))
            events.Add((m.Index, 1, null));
        foreach (Match m in EndRegex.Matches(sketch))
            events.Add((m.Index, 2, null));
        foreach (Match m in TheoremRegex.Matches(sketch))
            events.Add((m.Index, 3, m.Groups[1].Value));
        events.Sort((a, b) => a.Offset.CompareTo(b.Offset));

        var stack = new List<(bool IsNamespace, string? Name)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();

        foreach (var (_, kind, value) in events)
        {
            switch (kind)
            {
                case 0: stack.Add((true, value)); break;
                case 1: stack.Add((false, null)); break;
                case 2: if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); break;
                case 3:
                    var prefix = string.Join('.', stack.Where(s => s.IsNamespace).Select(s => s.Name));
                    var qualified = prefix.Length == 0 ? value! : $"{prefix}.{value}";
                    if (seen.Add(qualified)) result.Add(qualified);
                    break;
            }
        }
        return result;
    }

    private static readonly Regex NamespaceOpenRegex = new(
        @"(?m)^\s*namespace\s+([A-Za-z_][\w.]*)", RegexOptions.Compiled);
    private static readonly Regex SectionOpenRegex = new(
        @"(?m)^\s*section\b", RegexOptions.Compiled);
    private static readonly Regex EndRegex = new(
        @"(?m)^\s*end\b", RegexOptions.Compiled);

    internal readonly record struct Declaration(string Name, string Signature);

    // Captures the keyword+name, then lazily everything up to the first top-level
    // ':=' as the signature (binders + ':' + type). Doesn't track paren/bracket
    // depth, so a type containing its own literal ':=' (e.g. an inline `let` in a
    // dependent type) would truncate early — a known approximation consistent with
    // the rest of this file's regex-based (not full-parser) approach.
    private static readonly Regex TheoremRegex = new(
        @"\b(?:theorem|lemma)\s+([A-Za-z_][\w.']*)((?:.|\n)*?):=",
        RegexOptions.Compiled);

    // Value-level declaration kinds that can shadow an identifier a goal's type
    // depends on. `theorem`/`lemma` are deliberately excluded — they can't silently
    // change what an existing `def` means, only add new propositions.
    private static readonly Regex AuxDeclRegex = new(
        @"\b(?:noncomputable\s+)?(?:def|abbrev|instance|axiom|structure|inductive|class)\s+([A-Za-z_][\w.']*)",
        RegexOptions.Compiled);

    internal static IReadOnlyList<Declaration> TheoremDeclarationsIn(string sketch)
    {
        var results = new List<Declaration>();
        foreach (Match m in TheoremRegex.Matches(sketch))
            results.Add(new Declaration(m.Groups[1].Value, m.Groups[2].Value));
        return results;
    }

    private static HashSet<string> AuxiliaryDeclarationNamesIn(string sketch)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in AuxDeclRegex.Matches(sketch))
            names.Add(m.Groups[1].Value);
        return names;
    }

    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Internal (not private) so <see cref="Planning.ProofGoalGraph"/> can reuse the
    /// same normalization for goal-text hashing — one whitespace convention, not two.</summary>
    internal static string NormalizeWhitespace(string s) =>
        WhitespaceRun.Replace(s, " ").Trim();
}
