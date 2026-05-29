using System.Diagnostics;

namespace NexusAgent.Core.Oracle;

/// <summary>
/// Shared launch primitive: PATH setup, spawn, timeout, kill, stdout/stderr capture.
/// Extracted from <see cref="LeanOracle"/> so that <c>HgParallelSolver</c> can spawn
/// the same Lean environment without taking a dependency on the full oracle.
/// </summary>
public static class LeanProcessLauncher
{
    /// <summary>
    /// Launches <c>lake env lean &lt;file&gt;</c> from <paramref name="workingDirectory"/>,
    /// captures stdout and stderr, and returns both along with the exit code.
    /// Kills the process (entire tree) if <paramref name="ct"/> is cancelled.
    /// </summary>
    public static async Task<LeanProcessResult> RunAsync(
        string leanFile,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? extraEnv = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // elan installs lake to ~/.elan/bin — add it to PATH so child processes
        // can find lake even when the parent shell didn't source elan.
        var elanBin = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".elan", "bin");
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (!pathEnv.Contains(elanBin))
            pathEnv = $"{elanBin}:{pathEnv}";

        var psi = new ProcessStartInfo
        {
            FileName               = "lake",
            Arguments              = $"env lean {leanFile}",
            WorkingDirectory       = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        psi.Environment["PATH"] = pathEnv;

        if (extraEnv is not null)
            foreach (var (k, v) in extraEnv)
                psi.Environment[k] = v;

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start lake process");

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* race */ }
            sw.Stop();
            return new LeanProcessResult("", "", -1, sw.Elapsed, TimedOut: true);
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        sw.Stop();

        return new LeanProcessResult(stdout, stderr, proc.ExitCode, sw.Elapsed, TimedOut: false);
    }
}

/// <summary>Result of a raw <c>lake env lean</c> invocation.</summary>
public sealed record LeanProcessResult(
    string Stdout,
    string Stderr,
    int    ExitCode,
    TimeSpan Elapsed,
    bool   TimedOut);
