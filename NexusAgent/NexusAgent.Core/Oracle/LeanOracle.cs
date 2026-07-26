using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexusAgent.Core.Configuration;
using NexusAgent.Core.Memory;
using NexusAgent.Core.Models;

namespace NexusAgent.Core.Oracle;

/// <summary>
/// Shells out to `lake build` / `lean --run` and parses the output. Caches
/// compile results in Neo4j keyed by SHA-256 of the sketch — identical sketches
/// never recompile, which eliminates a large fraction of duplicate work
/// across episodes.
/// </summary>
public sealed partial class LeanOracle : ILeanOracle
{
    private readonly NexusConfig _config;
    private readonly INeo4jClient _neo4j;
    private readonly ILogger<LeanOracle> _log;

    public LeanOracle(
        IOptions<NexusConfig> config,
        INeo4jClient neo4j,
        ILogger<LeanOracle> log)
    {
        _config = config.Value;
        _neo4j = neo4j;
        _log = log;
    }

    public async Task<LeanResult> CompileAsync(string leanSketch, CancellationToken ct)
    {
        var hash = ComputeSha256(leanSketch);

        var cached = await _neo4j.GetCompileCacheAsync(hash, ct);
        LeanResult result;
        if (cached is not null)
        {
            _log.LogDebug("LeanOracle cache hit: {Hash}", hash[..12]);
            result = cached;
        }
        else
        {
            result = await CompileFreshAsync(leanSketch, ct);
            await _neo4j.PutCompileCacheAsync(hash, result, ct);
        }

        // The compile cache doesn't store HasSorryAxiom, and a cached "false" isn't
        // trustworthy either way — always re-verify when the sketch otherwise looks
        // fully proved. Cheap in aggregate: this only fires on apparent successes,
        // a small fraction of all compiles.
        if (result.Compiled && result.SorryCount == 0 && result.RemainingGoals == 0
            && await HasSorryAxiomAsync(leanSketch, ct))
        {
            _log.LogWarning(
                "LeanOracle: sketch looked fully proved but #print axioms found sorryAx " +
                "(likely a tactic citing an already-sorry-backed declaration) — rejecting");
            result = result with { HasSorryAxiom = true };
        }

        return result;
    }

    public async Task<LeanResult> CheckSubgoalAsync(
        string goalStatement,
        string proofTactics,
        IEnumerable<string> imports,
        CancellationToken ct)
    {
        var importBlock = string.Join("\n", imports.Select(i => $"import {i}"));
        var fullSketch =
            $"""
            {importBlock}

            example : {goalStatement} := by
            {Indent(proofTactics, 2)}
            """;
        return await CompileAsync(fullSketch, ct);
    }

    private async Task<LeanResult> CompileFreshAsync(string sketch, CancellationToken ct)
    {
        var (stdout, stderr, exitCode, elapsed, timedOut) = await RunLeanAsync(sketch, ct);
        if (timedOut)
            return LeanResult.Failure("Lean compile timed out", elapsed);
        return ParseLeanOutput(stdout, stderr, exitCode, elapsed);
    }

    /// <summary>
    /// Runs a sketch with `#print axioms &lt;name&gt;` appended for every theorem/lemma
    /// it declares, and reports whether any of them transitively depend on
    /// <c>sorryAx</c>. This catches a class of false "solved" verdict that
    /// <see cref="ParseLeanOutput"/>'s sorry-count scan cannot: a tactic that cites
    /// a different, already-sorry-backed declaration (e.g. `exact SomeOther.thm`)
    /// compiles clean with no "declaration uses 'sorry'" warning at the citing
    /// site — Lean only prints that warning where the literal `sorry` occurs, not
    /// at every downstream use. `#print axioms` is the only reliable check.
    /// Confirmed live 2026-07-25: a candidate citing `Green14.W_3_15` (itself
    /// `:= by sorry` in its source file) compiled with SorryCount=0 and no
    /// warning, but `#print axioms` showed `sorryAx` in its dependency list.
    /// </summary>
    private async Task<bool> HasSorryAxiomAsync(string sketch, CancellationToken ct)
    {
        var names = NexusAgent.Core.Agent.SketchValidator.TheoremNamesIn(sketch);
        if (names.Count == 0) return false;

        var probe = sketch + "\n\n" + string.Join("\n", names.Select(n => $"#print axioms {n}"));
        var (stdout, stderr, _, _, timedOut) = await RunLeanAsync(probe, ct);
        if (timedOut) return false; // can't confirm either way; don't block on a timeout here
        return (stdout + "\n" + stderr).Contains("sorryAx");
    }

    private async Task<(string Stdout, string Stderr, int ExitCode, TimeSpan Elapsed, bool TimedOut)>
        RunLeanAsync(string sketch, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Temp files live inside a _nexus_tmp folder at the lake project root so
        // `lake env lean` can resolve Mathlib / FormalConjectures imports from the
        // workspace cache.  The folder is created on first use.
        var tmpDir = Path.Combine(_config.LeanProjectPath, "_nexus_tmp");
        Directory.CreateDirectory(tmpDir);
        var tempFile = Path.Combine(tmpDir, $"Tmp_{Guid.NewGuid():N}.lean");
        await File.WriteAllTextAsync(tempFile, sketch, ct);

        try
        {
            // elan installs lake to ~/.elan/bin — add it to PATH so the child
            // process can find it even when the parent shell didn't source elan.
            var elanBin = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".elan", "bin");
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!pathEnv.Contains(elanBin))
                pathEnv = $"{elanBin}:{pathEnv}";

            var psi = new ProcessStartInfo
            {
                FileName = "lake",
                Arguments = $"env lean {tempFile}",
                WorkingDirectory = _config.LeanProjectPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.Environment["PATH"] = pathEnv;

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start lake process");

            using var timeout = new CancellationTokenSource(_config.LeanCompileTimeout);
            using var combined = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(combined.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(combined.Token);

            try
            {
                await proc.WaitForExitAsync(combined.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* race */ }
                sw.Stop();
                return ("", "", -1, sw.Elapsed, true);
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            sw.Stop();

            return (stdout, stderr, proc.ExitCode, sw.Elapsed, false);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { /* ignore */ }
        }
    }

    private static LeanResult ParseLeanOutput(
        string stdout, string stderr, int exitCode, TimeSpan elapsed)
    {
        var combined = stdout + "\n" + stderr;
        var errors = new List<string>();
        var warnings = new List<string>();
        var pendingGoals = new List<string>();
        var sorryCount = 0;

        // Parse diagnostics as multi-line blocks. A Lean 4 error looks like:
        //   problem.lean:42:5: error: type mismatch
        //     h.left
        //   has type
        //     P x : Prop
        //   but is expected to have type
        //     Q x : Prop
        // Capturing only the header line discards the context the LLM needs to fix
        // the proof. We collect all continuation lines until the next diagnostic.
        var lines = combined.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (ErrorPattern().IsMatch(line))
            {
                bool isUnsolvedGoals = UnsolvedGoalsPattern().IsMatch(line);
                var blockLines = new List<string> { line.Trim() };
                // For "unsolved goals" blocks specifically, Lean blank-line-separates
                // multiple goals (each preceded by an optional `case <tag>` header) —
                // preserve those blank lines here so they can be split back out into
                // one PendingGoalTexts entry per goal below. General error blocks keep
                // the existing blank-line-discarding behavior for `errors` (unaffected
                // by this list — errors.Add below still uses blockLines).
                var rawGoalLines = isUnsolvedGoals ? new List<string>() : null;
                while (i + 1 < lines.Length && !IsDiagnosticHeader(lines[i + 1]))
                {
                    i++;
                    if (!string.IsNullOrWhiteSpace(lines[i]))
                        blockLines.Add(lines[i]);
                    rawGoalLines?.Add(lines[i]);
                }
                errors.Add(string.Join('\n', blockLines));
                // For "unsolved goals" errors, expose each goal's raw text (case tag +
                // local hypotheses + `⊢ …`) as its own PendingGoalTexts entry, instead
                // of joining every goal in the block into one string. Confirmed live
                // 2026-07-26: a `refine ⟨?_, ?_⟩`-style split prints as
                // "case refine_1\n⊢ P1\n\ncase refine_2\n⊢ P2" — blank-line-separated.
                // If a future Lean version doesn't blank-line-separate, this falls back
                // to one entry for the whole block (same as the prior behavior), not a
                // crash — known limitation, not yet observed in practice.
                if (isUnsolvedGoals && rawGoalLines is { Count: > 0 })
                {
                    foreach (var group in SplitOnBlankLines(rawGoalLines))
                        if (group.Count > 0)
                            pendingGoals.Add(string.Join('\n', group));
                }
            }
            else if (SorryPattern().IsMatch(line))
                sorryCount++;
            else if (WarningPattern().IsMatch(line))
                warnings.Add(line.Trim());
        }

        // Lean prints "declaration uses 'sorry'" — that's a warning, not an error.
        // True compile success ⇔ exit code = 0 AND no error diagnostics present.
        // (Previously `||` — that let any failure whose error diagnostics went
        // unrecognized by ErrorPattern silently pass as compiled=true. Confirmed
        // exploitable 2026-07-25: a real `exitCode=1` synthInstanceFailed error,
        // in Lean's `error(code):` format which ErrorPattern didn't match, was
        // reported as compiled=true with errors=[].)
        var compiled = exitCode == 0 && errors.Count == 0;

        return new LeanResult
        {
            Compiled = compiled,
            RemainingGoals = pendingGoals.Count,
            SorryCount = sorryCount,
            Errors = errors.ToArray(),
            Warnings = warnings.ToArray(),
            CompileTime = elapsed,
            PendingGoalTexts = pendingGoals.ToArray(),
        };
    }

    /// <summary>Splits a line list into groups separated by blank lines, dropping the
    /// blank separators themselves and any leading/trailing empty groups.</summary>
    private static List<List<string>> SplitOnBlankLines(List<string> lines)
    {
        var groups = new List<List<string>>();
        var current = new List<string>();
        foreach (var l in lines)
        {
            if (string.IsNullOrWhiteSpace(l))
            {
                if (current.Count > 0)
                {
                    groups.Add(current);
                    current = new List<string>();
                }
            }
            else
            {
                current.Add(l);
            }
        }
        if (current.Count > 0) groups.Add(current);
        return groups;
    }

    private static string ComputeSha256(string input)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Indent(string text, int spaces)
    {
        var pad = new string(' ', spaces);
        return string.Join("\n", text.Split('\n').Select(l => pad + l));
    }

    // Matches both the plain `error:` form and the newer `error(diagnostic-code):`
    // form (e.g. `error(lean.synthInstanceFailed):`) — the parenthesized code is optional.
    [GeneratedRegex(@"^.*\.lean:\d+:\d+:\s*error(\([^)]*\))?:", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorPattern();

    [GeneratedRegex(@"^.*\.lean:\d+:\d+:\s*warning(\([^)]*\))?:", RegexOptions.IgnoreCase)]
    private static partial Regex WarningPattern();

    [GeneratedRegex(@"declaration uses 'sorry'|contains sorry", RegexOptions.IgnoreCase)]
    private static partial Regex SorryPattern();

    [GeneratedRegex(@"unsolved goals", RegexOptions.IgnoreCase)]
    private static partial Regex UnsolvedGoalsPattern();

    /// <summary>Returns true if <paramref name="line"/> starts a new Lean diagnostic block.</summary>
    private static bool IsDiagnosticHeader(string line) =>
        ErrorPattern().IsMatch(line) || WarningPattern().IsMatch(line) || SorryPattern().IsMatch(line);
}
