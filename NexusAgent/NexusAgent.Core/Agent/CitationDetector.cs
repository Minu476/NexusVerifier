using System.Text.RegularExpressions;

namespace NexusAgent.Core.Agent;

/// <summary>
/// Classification of a compiled, sorry-free proof against the theorem's own original
/// (pre-stripping) declaration.
/// </summary>
public enum CitationVerdict
{
    /// <summary>The proof is (up to trivial wrapping) `exact &lt;original declaration&gt;` —
    /// the original complete proof is still in scope because the stub imports the file that
    /// defines it, so this "solve" did no proof search.</summary>
    Citation,

    /// <summary>The proof does not reduce to citing the original declaration. Citing a
    /// different, genuine helper lemma is INDEPENDENT, not CITATION — only naming this
    /// problem's own original target is the exploit.</summary>
    Independent,

    /// <summary>No proof text, or nothing could be classified.</summary>
    Unknown,
}

/// <summary>
/// Solve-time citation-exploit gate. This is a C# port of the post-hoc
/// <c>scripts/citation_audit.py</c>, promoted from an after-the-fact audit into an inline
/// check the orchestrator applies before ever reporting <see cref="Models.ProofOutcome.Solved"/>.
///
/// Why this exists (spec's Task 3, docs/SESSION_HANDOFF_2026-08-03.md): FC100 stub problems
/// import the file that defines the original, complete proof (needed for the type/definitions
/// the stub statement uses), so a stub can be "solved" by citing that original declaration —
/// e.g. `exact ame_2_exists hd`. This compiles, is axiom-clean (the original isn't sorry-backed),
/// and passes the structural gate (the statement is unchanged) — none of Topos's other
/// correctness checks catch it, because nothing about it is structurally wrong. It just isn't
/// proof search. Documented in docs/FC100_GAP_SET_METHODOLOGY.md and confirmed in the
/// 2026-08-03 pivot-gate A/B, where 100% of both arms' raw "solves" were this exploit.
///
/// Detect, not prevent: this reports the exploit as a distinct outcome rather than making it
/// structurally impossible (e.g. by renaming/removing the original declaration when the stub is
/// built). Prevention is stronger and is the better long-term fix, but changes how the corpus is
/// generated, which is out of scope for this pass — see docs/SESSION_HANDOFF_2026-08-03.md.
/// </summary>
public static class CitationDetector
{
    /// <summary>
    /// Extracts the original (pre-stripping) declaration's bare name from a stub's own header
    /// comment, e.g. <c>/-! Stripped stub: Arxiv.«1308.0994».KTExtendsK → 'g_KTExtendsK_new' -/</c>
    /// → <c>"KTExtendsK"</c>. Same source convention <see cref="Program.ExtractStatement"/>
    /// already reads the statement preview from (NexusAgent.Cli/Program.cs).
    ///
    /// Returns null if the file has no such header (e.g. a hand-authored non-stub problem) —
    /// callers must treat that as "no citation check possible", not as "not a citation".
    /// </summary>
    public static string? ExtractOriginalDeclarationName(string fullSketch)
    {
        var m = Regex.Match(fullSketch, @"Stripped stub:\s*([^\s→]+)\s*→", RegexOptions.None);
        if (!m.Success) return null;
        return BareName(m.Groups[1].Value.Trim());
    }

    /// <summary>
    /// Last dot-separated segment of a (possibly guillemet-quoted) qualified Lean name, e.g.
    /// <c>Arxiv.«1308.0994».KTExtendsK</c> → <c>KTExtendsK</c>, <c>OeisA67720.a_6</c> →
    /// <c>a_6</c>. Dots *inside* a «...» segment (Lean's escape for identifiers containing
    /// characters that aren't normally identifier characters, e.g. arXiv IDs) are not segment
    /// separators — «1308.0994» is one name component, not two.
    /// </summary>
    private static string BareName(string qualified)
    {
        int lastSplit = -1;
        bool inGuillemets = false;
        for (int i = 0; i < qualified.Length; i++)
        {
            switch (qualified[i])
            {
                case '«': inGuillemets = true; break;
                case '»': inGuillemets = false; break;
                case '.' when !inGuillemets: lastSplit = i; break;
            }
        }
        return lastSplit < 0 ? qualified : qualified[(lastSplit + 1)..];
    }

    /// <summary>
    /// Isolates the target theorem's proof term/tactic block from a full compiled Lean source
    /// (imports + namespace + theorem signature + proof + `end`), so <see cref="Classify"/> only
    /// sees the proof itself — passing the whole file inflates its distinct-tactic-verb count
    /// with "import"/"namespace"/"theorem"/"end" and the citation check never fires.
    ///
    /// Takes the first PAREN-DEPTH-ZERO `:=` (the theorem signature's own proof-start
    /// assignment) — not the first `:=` overall, which can appear inside binder syntax like
    /// `(n : Nat)`, and not the last, which can land inside named-argument syntax like
    /// `(α := α)` in the winning tactic itself and truncate the real proof. Cuts at the next
    /// top-level `end` line.
    /// </summary>
    public static string ExtractProofBody(string fullSketch)
    {
        if (string.IsNullOrEmpty(fullSketch)) return fullSketch;

        var cleaned = StripComments(fullSketch);
        var idx = FindFirstTopLevelAssign(cleaned);
        if (idx < 0) return cleaned.Trim();

        var body = cleaned[(idx + 2)..];
        var endMatch = Regex.Match(body, @"\n\s*end\b");
        if (endMatch.Success) body = body[..endMatch.Index];
        return body.Trim();
    }

    private static string StripComments(string text)
    {
        text = Regex.Replace(text, @"/-.*?-/", "", RegexOptions.Singleline);
        text = Regex.Replace(text, @"--.*$", "", RegexOptions.Multiline);
        return text;
    }

    /// <summary>Index of the first `:=` not nested inside (), [], or {} — the signature's own
    /// proof-start assignment, not a binder default or a `let x := ...` inside the tactic
    /// block.</summary>
    private static int FindFirstTopLevelAssign(string text)
    {
        int depth = 0;
        for (int i = 0; i < text.Length - 1; i++)
        {
            switch (text[i])
            {
                case '(' or '[' or '{': depth++; break;
                case ')' or ']' or '}': depth = Math.Max(0, depth - 1); break;
                case ':' when text[i + 1] == '=' && depth == 0: return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Classifies a winning proof body against the problem's own original declaration's bare
    /// name. A proof is <see cref="CitationVerdict.Citation"/> when it names that specific
    /// declaration AND is otherwise trivial (at most 3 distinct leading tactic verbs across its
    /// lines) — i.e. `exact &lt;orig&gt;`, `exact @&lt;orig&gt;`, `simpa using &lt;orig&gt;`, or
    /// a one- or two-line wrapper around it. A longer, varied proof that happens to cite the
    /// original among other genuine steps is <see cref="CitationVerdict.Independent"/>: citing a
    /// real fact and doing real proof search around it is not the exploit this catches.
    /// </summary>
    public static CitationVerdict Classify(string? proofText, string originalBareName)
    {
        if (string.IsNullOrWhiteSpace(proofText)) return CitationVerdict.Unknown;

        var cleaned = StripComments(proofText);
        var lines = cleaned.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToArray();
        if (lines.Length == 0) return CitationVerdict.Unknown;

        var blob = string.Join(' ', lines);
        var mentionsOrig = Regex.IsMatch(blob, @"\b" + Regex.Escape(originalBareName) + @"\b");
        if (!mentionsOrig) return CitationVerdict.Independent;

        // NOTE: the Python original (scripts/citation_audit.py) had a
        // "trivial-tactic-without-mentioning-orig ⇒ independent" branch here that was
        // unreachable dead code — by this point mentionsOrig is always true (the `if
        // (!mentionsOrig)` above already returned), so a condition requiring `!mentionsOrig`
        // inside this branch could never fire. Not ported; see git history for the Python if
        // the original behavior needs to be re-derived.

        var tacticVerbs = new HashSet<string>();
        foreach (var line in lines)
        {
            var firstWord = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (firstWord is not null) tacticVerbs.Add(firstWord);
        }

        return mentionsOrig && tacticVerbs.Count <= 3
            ? CitationVerdict.Citation
            : CitationVerdict.Independent;
    }
}
