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
        if (cached is not null)
        {
            _log.LogDebug("LeanOracle cache hit: {Hash}", hash[..12]);
            return cached;
        }

        var result = await CompileFreshAsync(leanSketch, ct);
        await _neo4j.PutCompileCacheAsync(hash, result, ct);
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
                return LeanResult.Failure("Lean compile timed out", sw.Elapsed);
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            sw.Stop();

            return ParseLeanOutput(stdout, stderr, proc.ExitCode, sw.Elapsed);
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

        foreach (var line in combined.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (ErrorPattern().IsMatch(line))
                errors.Add(line.Trim());
            else if (WarningPattern().IsMatch(line))
                warnings.Add(line.Trim());
            else if (SorryPattern().IsMatch(line))
                sorryCount++;
            else if (UnsolvedGoalsPattern().IsMatch(line))
                pendingGoals.Add(line.Trim());
        }

        // Lean prints "declaration uses 'sorry'" — that's a warning, not an error.
        // True compile failure ⇔ exit code ≠ 0 AND error pattern present.
        var compiled = exitCode == 0 || errors.Count == 0;

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

    [GeneratedRegex(@"^.*\.lean:\d+:\d+:\s*error:", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorPattern();

    [GeneratedRegex(@"^.*\.lean:\d+:\d+:\s*warning:", RegexOptions.IgnoreCase)]
    private static partial Regex WarningPattern();

    [GeneratedRegex(@"declaration uses 'sorry'|contains sorry", RegexOptions.IgnoreCase)]
    private static partial Regex SorryPattern();

    [GeneratedRegex(@"unsolved goals", RegexOptions.IgnoreCase)]
    private static partial Regex UnsolvedGoalsPattern();
}
