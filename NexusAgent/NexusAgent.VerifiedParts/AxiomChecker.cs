using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexusAgent.Core.Configuration;
using NexusAgent.Core.Oracle;

namespace NexusAgent.VerifiedParts;

/// <summary>
/// Appends <c>#print axioms</c> to a proof sketch, runs <c>lake env lean</c>,
/// and returns the axiom closure of the declaration.
///
/// Self-contained: uses <see cref="LeanProcessLauncher"/> directly so it does not
/// need any modification to <see cref="ILeanOracle"/>. The LEGO piece owns this.
/// </summary>
public sealed class AxiomChecker
{
    private readonly string _leanProjectPath;
    private readonly ILogger<AxiomChecker> _log;

    // Fixed local name used in every generated sketch — stable, un-colliding.
    private const string CheckDecl = "_nexus_axiom_check_";

    public AxiomChecker(IOptions<NexusConfig> config, ILogger<AxiomChecker> log)
    {
        _leanProjectPath = config.Value.LeanProjectPath;
        _log = log;
    }

    /// <summary>
    /// Compiles the proof sketch (imports + statement + proof) with
    /// <c>#print axioms</c> appended and returns the axiom names.
    ///
    /// Returns an empty array when the declaration has no non-logical axioms
    /// (i.e. "does not depend on any axioms").
    /// Returns <c>null</c> when the <c>#print axioms</c> line was not found in
    /// output — which means the compile failed before reaching it.
    /// </summary>
    public async Task<string[]?> CheckAsync(
        string importsHeader,
        string statementText,
        string proofBlock,
        CancellationToken ct)
    {
        var sketch = BuildAxiomSketch(importsHeader, statementText, proofBlock);

        // Use the OS temp dir so this works even when the Lean project tree is
        // mounted read-only (e.g. in Docker with a :ro volume).
        var tmpDir  = Path.Combine(Path.GetTempPath(), "_nexus_tmp");
        Directory.CreateDirectory(tmpDir);
        var tmpFile = Path.Combine(tmpDir, $"AxiomChk_{Guid.NewGuid():N}.lean");

        try
        {
            await File.WriteAllTextAsync(tmpFile, sketch, ct);
            var result = await LeanProcessLauncher.RunAsync(tmpFile, _leanProjectPath, ct: ct);

            _log.LogDebug("AxiomChecker lean exit={Exit} elapsed={Elapsed:F1}s",
                result.ExitCode, result.Elapsed.TotalSeconds);

            return ParseAxiomsFromOutput(result.Stdout + "\n" + result.Stderr);
        }
        finally
        {
            try { File.Delete(tmpFile); } catch { /* best-effort */ }
        }
    }

    // ── sketch generation ────────────────────────────────────────────────────

    private static string BuildAxiomSketch(string imports, string statement, string proof)
    {
        // Use a private definition so we don't pollute any namespace.
        // The proof block is used verbatim after `:=`.
        return
            $"""
            {imports}

            private noncomputable def {CheckDecl} : {statement} := {proof}

            #print axioms {CheckDecl}
            """;
    }

    // ── output parsing ───────────────────────────────────────────────────────

    /// <summary>
    /// Lean 4 emits one of:
    ///   '<name>' depends on axioms: [propext, Classical.choice, ...]
    ///   '<name>' does not depend on any axioms
    ///
    /// These appear in stdout as info messages, optionally prefixed with
    /// "file:line:col: information: ".
    /// </summary>
    private static string[]? ParseAxiomsFromOutput(string output)
    {
        if (output.Contains("does not depend on any axioms"))
            return [];

        // Lean 4 emits the axiom list across multiple lines when there are
        // several axioms, e.g.:
        //   '...' depends on axioms: [propext,
        //    Classical.choice,
        //    Quot.sound]
        // Collapse the entire output to one line so the bracket-pair search
        // always finds both '[' and ']' together.
        var flat = output.Replace('\n', ' ').Replace('\r', ' ');

        var marker = "depends on axioms: [";
        var idx = flat.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;

        var start = idx + marker.Length;
        var end   = flat.IndexOf(']', start);
        if (end <= start) return null;

        var axiomList = flat[start..end];
        return axiomList
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(a => a.Length > 0)
            .ToArray();
    }

    // ── well-known unsafe axiom names ────────────────────────────────────────

    public static bool ContainsSorryAx(string[] axioms) =>
        axioms.Any(a => a.Contains("sorryAx", StringComparison.OrdinalIgnoreCase));

    public static bool ContainsNativeEscape(string[] axioms) =>
        axioms.Any(a =>
            a.Contains("ofReduceBool",     StringComparison.OrdinalIgnoreCase) ||
            a.Contains("native",           StringComparison.OrdinalIgnoreCase) ||
            a.Contains("trustCompiler",    StringComparison.OrdinalIgnoreCase));
}
