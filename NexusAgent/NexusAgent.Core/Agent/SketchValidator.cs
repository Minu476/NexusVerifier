using System.Text.RegularExpressions;

namespace NexusAgent.Core.Agent;

/// <summary>
/// Structural validity checks that guard against reward hacking.
/// A "structurally valid" sketch preserves every theorem and lemma declaration
/// from the original — it modifies proof bodies only, not declarations.
///
/// Used by both <see cref="NexusProverSubagent"/> (per-turn gate) and
/// <see cref="NexusOrchestrator"/> (per-episode bestSketch gate).
/// </summary>
internal static class SketchValidator
{
    /// <summary>
    /// Returns true if <paramref name="candidateSketch"/> preserves every theorem
    /// and lemma name declared in <paramref name="originalSketch"/>.
    ///
    /// A fully-proved or partially-improved sketch that fails this check has replaced
    /// original declaration(s) with unrelated provable code (reward hacking) and must
    /// not be accepted as progress or fossilized.
    ///
    /// Vacuously true when the original has no theorem/lemma declarations.
    /// </summary>
    internal static bool IsStructurallyValid(string originalSketch, string candidateSketch)
    {
        var originalNames = TheoremNamesIn(originalSketch);
        if (originalNames.Count == 0) return true;
        return originalNames.All(name => candidateSketch.Contains(name));
    }

    /// <summary>
    /// Extracts distinct theorem and lemma names from <paramref name="sketch"/>.
    /// Uses a tightened regex that matches valid Lean identifiers only
    /// (letters, digits, underscores, dots, primes) to avoid capturing trailing
    /// punctuation such as the colon in <c>theorem foo:</c>.
    /// </summary>
    internal static IReadOnlyList<string> TheoremNamesIn(string sketch)
    {
        var matches = Regex.Matches(sketch, @"\b(?:theorem|lemma)\s+([A-Za-z_][\w.']*)");
        return matches.Select(m => m.Groups[1].Value).Distinct().ToList();
    }
}
