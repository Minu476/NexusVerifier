using NexusAgent.VerifiedParts;

namespace NexusAgent.Tests.VerifiedParts;

/// <summary>
/// Unit tests for <see cref="VerifiedPartsPlugin.ExtractParentProblemId"/>.
/// Covers every naming shape present in FC100 to guard against regressions
/// in the holdout filter.
/// </summary>
public class ParentProblemIdTests
{
    // ── exact key, covering every FC100 decl-name shape ──────────────────────

    [Theory]
    [InlineData("Erdos1074.erdos_1074.variants.EHSNumbers_init", "erdos1074")]  // canonical double-seg
    [InlineData("erdos_1074.variants.mem_pillaiPrimes",          "erdos1074")]  // short-form (our parts.json)
    [InlineData("Erdos26.not_isThick_of_finite",                 "erdos26")]    // bare lemma — was broken
    [InlineData("Erdos50.erdos_50_schoenberg",                   "erdos50")]    // underscore suffix — was broken
    [InlineData("Erdos1052.isUnitaryPerfect_60",                 "erdos1052")]
    [InlineData("Erdos697.density_exists",                       "erdos697")]
    [InlineData("Erdos835.johnsonGraph_18_9_chromaticNumber",    "erdos835")]
    [InlineData("Erdos1054.f_undefined_at_2",                    "erdos1054")]
    [InlineData("Mathoverflow75792.Reachable.complexity",        "mathoverflow75792")] // PascalCase struct — was broken
    [InlineData("OpenQuantumProblem23.sicOverlapSq_three",       "openquantumproblem23")] // non-Erdős — was broken
    [InlineData("OeisA67720.a_6",                                "oeisa67720")]
    [InlineData("Arxiv.\u00ab1308.0994\u00bb.KTExtendsK",        "arxiv")]      // guillemet — coarsened (see below)
    public void Key_isExpected(string decl, string expected)
        => Assert.Equal(expected, VerifiedPartsPlugin.ExtractParentProblemId(decl));

    // ── same problem across naming forms → MUST share a key ─────────────────

    [Theory]
    // The 1074 case: direct load-bearing sibling
    [InlineData("Erdos1074.erdos_1074.variants.EHSNumbers_init", "erdos_1074.variants.mem_pillaiPrimes")]
    // Erdős bare-lemma vs double-seg form of the same problem
    [InlineData("Erdos26.erdos_26.variants.rusza",               "Erdos26.not_isThick_of_finite")]
    [InlineData("Erdos50.erdos_50_schoenberg",                   "erdos_50.variants.foo")]
    [InlineData("Erdos697.erdos_697.parts.i",                    "Erdos697.density_exists")]
    // Non-Erdős: two targets under the same namespace must share a key
    [InlineData("OpenQuantumProblem23.bb84Family_not_isSICFamily","OpenQuantumProblem23.sicOverlapSq_three")]
    [InlineData("OeisA67720.a_1",                                "OeisA67720.a_6")]
    public void SameProblem_sharesKey(string a, string b)
        => Assert.Equal(
            VerifiedPartsPlugin.ExtractParentProblemId(a),
            VerifiedPartsPlugin.ExtractParentProblemId(b));

    // ── different problems → MUST NOT share a key ────────────────────────────

    [Theory]
    [InlineData("Erdos1074.erdos_1074.variants.X",         "Erdos1052.isUnitaryPerfect_60")]
    [InlineData("Erdos26.not_isThick_of_finite",           "Erdos263.erdos_263.variants.X")]  // 26 vs 263
    [InlineData("OpenQuantumProblem23.sicOverlapSq_three", "OpenQuantumProblem13.Qubit.firstCol_normSq")]
    [InlineData("OeisA67720.a_1",                          "OeisA6697.count_false_morphism")] // 67720 vs 6697
    public void DifferentProblems_differ(string a, string b)
        => Assert.NotEqual(
            VerifiedPartsPlugin.ExtractParentProblemId(a),
            VerifiedPartsPlugin.ExtractParentProblemId(b));

    // ── documented conservative coarsening ───────────────────────────────────
    // Distinct sub-problems under one top namespace collapse to a single key.
    // This is intentional: over-excluding an ingestible sibling is harmless for holdout;
    // under-excluding a load-bearing one is the leakage being guarded against.
    // Use VerifiedPart.ProblemId or exact-decl exclusion when finer control is needed.

    [Theory]
    [InlineData("WrittenOnTheWallII.GraphConjecture13.conjecture13",
                "WrittenOnTheWallII.GraphConjecture16.conjecture16")]
    [InlineData("Arxiv.\u00ab1308.0994\u00bb.KTExtendsK",
                "Arxiv.\u00ab1609.08688\u00bb.tripleProduct_const")]
    public void SameTopNamespace_coarsensTogether(string a, string b)
        => Assert.Equal(
            VerifiedPartsPlugin.ExtractParentProblemId(a),
            VerifiedPartsPlugin.ExtractParentProblemId(b));

    // ── edge cases ───────────────────────────────────────────────────────────

    [Fact]
    public void Empty_returnsEmpty()
        => Assert.Equal(string.Empty, VerifiedPartsPlugin.ExtractParentProblemId(""));

    [Fact]
    public void NoSeparator_returnsNormalized()
        => Assert.Equal("erdos1074", VerifiedPartsPlugin.ExtractParentProblemId("erdos_1074"));
}
