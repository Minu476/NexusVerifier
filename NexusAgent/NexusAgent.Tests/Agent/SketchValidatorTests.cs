using NexusAgent.Core.Agent;

namespace NexusAgent.Tests.Agent;

/// <summary>
/// Unit tests for <see cref="SketchValidator.IsStructurallyValid"/>. Includes
/// regression tests for two reward-hacking exploits confirmed live against real
/// FC100 gap-set problems on 2026-07-25 (see docs/TOPOS_INTEGRATION_REPORT.md-
/// adjacent session notes) — both slipped past the pre-fix substring-containment
/// check.
/// </summary>
public class SketchValidatorTests
{
    private const string OriginalSketch = """
        import FormalConjectures.OEIS.«228828»

        open OeisA228828

        theorem a_two_new : OeisA228828.a 2 = 7 := by
          sorry
        """;

    [Fact]
    public void GenuineProofBodyChange_isValid()
    {
        var candidate = """
            import FormalConjectures.OEIS.«228828»

            open OeisA228828

            theorem a_two_new : OeisA228828.a 2 = 7 := by
              unfold a
              decide
            """;
        Assert.True(SketchValidator.IsStructurallyValid(OriginalSketch, candidate));
    }

    [Fact]
    public void SubstringRename_isRejected()
    {
        // Regression: `oeis_a_two_new` contains `a_two_new` as a substring, which
        // the old `candidateSketch.Contains(name)` check accepted.
        var candidate = """
            import Mathlib

            namespace OeisA228828
            def a : ℕ → ℕ := λ n => 3*n+1
            end OeisA228828

            theorem oeis_a_two_new : OeisA228828.a 2 = 7 := by
              native_decide
            """;
        Assert.False(SketchValidator.IsStructurallyValid(OriginalSketch, candidate));
    }

    [Fact]
    public void DefShadowing_withExactNamePreserved_isRejected()
    {
        // Regression: same exploit as above, but with the original name kept
        // byte-identical — proves the def-shadowing ban is load-bearing on its
        // own, not just an artifact of the rename also being caught.
        var candidate = """
            import Mathlib

            namespace OeisA228828
            def a : ℕ → ℕ := λ n => 3*n+1
            end OeisA228828

            theorem a_two_new : OeisA228828.a 2 = 7 := by
              native_decide
            """;
        Assert.False(SketchValidator.IsStructurallyValid(OriginalSketch, candidate));
    }

    [Fact]
    public void UnrelatedPlaceholderTheorem_isRejected()
    {
        var candidate = """
            import Mathlib

            theorem A228828 : True := by trivial
            """;
        Assert.False(SketchValidator.IsStructurallyValid(OriginalSketch, candidate));
    }

    [Fact]
    public void TypeTextChanged_sameName_isRejected()
    {
        var candidate = """
            import FormalConjectures.OEIS.«228828»

            open OeisA228828

            theorem a_two_new : True := by
              trivial
            """;
        Assert.False(SketchValidator.IsStructurallyValid(OriginalSketch, candidate));
    }

    [Fact]
    public void WhitespaceOnlyReformatting_ofSignature_isValid()
    {
        var candidate = """
            import FormalConjectures.OEIS.«228828»

            open OeisA228828

            theorem a_two_new :
                OeisA228828.a  2   =  7 := by
              decide
            """;
        Assert.True(SketchValidator.IsStructurallyValid(OriginalSketch, candidate));
    }

    [Fact]
    public void NewAuxiliaryLemma_notShadowingAnything_isStillRejected()
    {
        // Conservative by design: any new def/abbrev/instance/axiom is banned,
        // even one that doesn't (as far as a regex can tell) shadow anything the
        // goal depends on. See class doc comment for the rationale.
        var candidate = """
            import FormalConjectures.OEIS.«228828»

            open OeisA228828

            def helper : ℕ := 7

            theorem a_two_new : OeisA228828.a 2 = 7 := by
              sorry
            """;
        Assert.False(SketchValidator.IsStructurallyValid(OriginalSketch, candidate));
    }

    [Fact]
    public void MultipleOriginalTheorems_allMustBePreserved()
    {
        const string original = """
            theorem foo : 1 = 1 := by sorry
            theorem bar : 2 = 2 := by sorry
            """;
        var candidateDropsOne = """
            theorem foo : 1 = 1 := by rfl
            theorem baz : 2 = 2 := by rfl
            """;
        Assert.False(SketchValidator.IsStructurallyValid(original, candidateDropsOne));

        var candidateKeepsBoth = """
            theorem foo : 1 = 1 := by rfl
            theorem bar : 2 = 2 := by rfl
            """;
        Assert.True(SketchValidator.IsStructurallyValid(original, candidateKeepsBoth));
    }

    [Fact]
    public void NoTheoremsInOriginal_vacuouslyValid()
    {
        Assert.True(SketchValidator.IsStructurallyValid("import Mathlib", "import Mathlib\ntheorem x : True := by trivial"));
    }

    [Fact]
    public void QualifiedTheoremNamesIn_FlatTheorem_ReturnsBareName()
    {
        const string sketch = "theorem g_x_new : True := by sorry";
        Assert.Equal(["g_x_new"], SketchValidator.QualifiedTheoremNamesIn(sketch));
    }

    [Fact]
    public void QualifiedTheoremNamesIn_NamespaceWrapped_PrefixesWithNamespace()
    {
        const string sketch = """
            namespace MyRepro
            theorem main_thm : True := by sorry
            end MyRepro
            """;
        Assert.Equal(["MyRepro.main_thm"], SketchValidator.QualifiedTheoremNamesIn(sketch));
    }

    [Fact]
    public void QualifiedTheoremNamesIn_NestedNamespaces_PrefixesWithFullPath()
    {
        const string sketch = """
            namespace A
            namespace B
            theorem foo : True := by trivial
            end B
            end A
            """;
        Assert.Equal(["A.B.foo"], SketchValidator.QualifiedTheoremNamesIn(sketch));
    }

    [Fact]
    public void QualifiedTheoremNamesIn_SectionInsideNamespace_ConsumesEndButNoPrefix()
    {
        const string sketch = """
            namespace A
            section
            theorem foo : True := by trivial
            end
            end A
            """;
        Assert.Equal(["A.foo"], SketchValidator.QualifiedTheoremNamesIn(sketch));
    }

    [Fact]
    public void QualifiedTheoremNamesIn_NamespaceThenFlat_OnlyQualifiesWhatsInside()
    {
        const string sketch = """
            namespace A
            theorem foo : True := by trivial
            end A
            theorem bar : True := by trivial
            """;
        Assert.Equal(["A.foo", "bar"], SketchValidator.QualifiedTheoremNamesIn(sketch));
    }
}
