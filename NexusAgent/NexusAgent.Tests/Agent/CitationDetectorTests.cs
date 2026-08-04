using NexusAgent.Core.Agent;

namespace NexusAgent.Tests.Agent;

/// <summary>
/// Task 3 of docs/SESSION_HANDOFF_2026-08-03.md: promoting scripts/citation_audit.py's
/// post-hoc classification into a solve-time gate. These tests pin the C# port's behavior;
/// the port was also validated against the real, known-correct 13-independent/4-citation
/// baseline split (data/results/bench-2026-08-03_09-58-21.json) via a one-off scratch check —
/// reproduced exactly (13/4/0 unknown) before these fixture-based tests were written.
/// </summary>
public sealed class CitationDetectorTests
{
    // ── ExtractOriginalDeclarationName ──────────────────────────────────────────────

    [Fact]
    public void ExtractOriginalDeclarationName_SimpleQualifiedName_ReturnsBareName()
    {
        var sketch = "/-! Stripped stub: OeisA67720.a_6 → 'g_a_6_new' -/\ntheorem g_a_6_new : True := by trivial";

        Assert.Equal("a_6", CitationDetector.ExtractOriginalDeclarationName(sketch));
    }

    [Fact]
    public void ExtractOriginalDeclarationName_GuillemetQuotedSegment_DotInsideIsNotASeparator()
    {
        // Arxiv.«1308.0994».KTExtendsK — the dot inside "1308.0994" is part of one guillemet
        // segment (Lean's escape for identifiers that aren't normal identifier characters,
        // here an arXiv ID), not a namespace separator. The bare name is still just the last
        // real segment, "KTExtendsK".
        var sketch = "/-! Stripped stub: Arxiv.«1308.0994».KTExtendsK → 'g_KTExtendsK_new' -/";

        Assert.Equal("KTExtendsK", CitationDetector.ExtractOriginalDeclarationName(sketch));
    }

    [Fact]
    public void ExtractOriginalDeclarationName_AlternatePhrasing_StillParses()
    {
        // Some corpora use "→ proof reconstruction target 'Y'" instead of "→ 'Y'" — the
        // regex only needs the ORIGINAL name (before the arrow), so the phrasing after it
        // doesn't matter, but both real forms are covered here.
        var sketch = "/-! Stripped stub: OpenQuantumProblem35.ame_2_exists → proof reconstruction target 'g_ame_2_exists_new' -/";

        Assert.Equal("ame_2_exists", CitationDetector.ExtractOriginalDeclarationName(sketch));
    }

    [Fact]
    public void ExtractOriginalDeclarationName_NoHeader_ReturnsNull()
    {
        var sketch = "theorem foo : True := by trivial";

        Assert.Null(CitationDetector.ExtractOriginalDeclarationName(sketch));
    }

    // ── ExtractProofBody ─────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractProofBody_NamedArgumentSyntaxInTactic_DoesNotTruncateEarly()
    {
        // A naive "last :=" search lands inside "(α := α)" in the tactic block and truncates
        // the real proof. The first TOP-LEVEL (paren-depth 0) ":=" is the signature's own
        // proof-start assignment and must be used instead.
        var sketch = """
            theorem foo (α : Type) : True := by
              have h := bar (α := α)
              trivial
            end
            """;

        var body = CitationDetector.ExtractProofBody(sketch);

        Assert.Contains("have h := bar (α := α)", body);
        Assert.Contains("trivial", body);
    }

    [Fact]
    public void ExtractProofBody_CutsAtTopLevelEnd()
    {
        var sketch = "theorem foo : True := by\n  trivial\n\nend Namespace\n\n-- trailing junk";

        var body = CitationDetector.ExtractProofBody(sketch);

        Assert.DoesNotContain("trailing junk", body);
    }

    // ── Classify — the actual gate ──────────────────────────────────────────────────

    [Fact]
    public void Classify_BareCitation_IsCitation()
    {
        Assert.Equal(CitationVerdict.Citation,
            CitationDetector.Classify("by\n  exact ame_2_exists hd", "ame_2_exists"));
    }

    [Fact]
    public void Classify_QualifiedCitation_IsCitation()
    {
        Assert.Equal(CitationVerdict.Citation,
            CitationDetector.Classify("by exact Erdos1054.f_undefined_at_2", "f_undefined_at_2"));
    }

    [Fact]
    public void Classify_TrivialWrapperAroundCitation_IsStillCitation()
    {
        // simpa-wrapping the citation is still just the exploit with extra syntax — ≤3
        // distinct tactic verbs, still trivially reduces to citing the original.
        Assert.Equal(CitationVerdict.Citation,
            CitationDetector.Classify("by\n  simpa using flt_of_beal_conjecture H", "flt_of_beal_conjecture"));
    }

    [Fact]
    public void Classify_GenuineProof_NotMentioningOriginal_IsIndependent()
    {
        Assert.Equal(CitationVerdict.Independent,
            CitationDetector.Classify("by\n  induction n with\n  | zero => rfl\n  | succ k ih => simp [ih]", "a_6"));
    }

    [Fact]
    public void Classify_CitesADifferentHelperLemma_IsIndependentNotCitation()
    {
        // The boundary the handoff explicitly calls out: citing a genuine, DIFFERENT helper
        // lemma is legitimate mathematics, not this exploit. Only citing THIS problem's own
        // original target counts. Here the target is "ame_2_exists" but the proof cites an
        // unrelated lemma "some_other_helper_lemma" — must not be flagged.
        var verdict = CitationDetector.Classify("by exact some_other_helper_lemma hd", "ame_2_exists");

        Assert.Equal(CitationVerdict.Independent, verdict);
    }

    [Fact]
    public void Classify_LongerProofThatHappensToMentionOriginal_IsIndependent()
    {
        // Mentions the original among genuinely varied, multi-step reasoning (> 3 distinct
        // tactic verbs) — a real derivation that references the original fact in passing is
        // not the same as the proof BEING nothing but that citation.
        var proof = """
            by
              have h1 := ame_2_exists hd
              induction d with
              | zero => simp
              | succ k ih => omega
            """;

        Assert.Equal(CitationVerdict.Independent, CitationDetector.Classify(proof, "ame_2_exists"));
    }

    [Fact]
    public void Classify_NoProofText_IsUnknown()
    {
        Assert.Equal(CitationVerdict.Unknown, CitationDetector.Classify(null, "foo"));
        Assert.Equal(CitationVerdict.Unknown, CitationDetector.Classify("   ", "foo"));
    }

    [Fact]
    public void Classify_MentionsOriginalAsSubstringOnly_DoesNotFalsePositive()
    {
        // Word-boundary matching: "a_65" must not match a check for "a_6".
        Assert.Equal(CitationVerdict.Independent,
            CitationDetector.Classify("by exact a_65", "a_6"));
    }
}
