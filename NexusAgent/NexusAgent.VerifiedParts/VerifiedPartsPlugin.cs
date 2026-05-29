using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusAgent.VerifiedParts.Models;
using NexusAgent.VerifiedParts.Sinks;

namespace NexusAgent.VerifiedParts;

/// <summary>
/// The LEGO connector — two public surface points used by <c>NexusAgent.Cli/Program.cs</c>:
///
///   1.  builder.Services.AddVerifiedParts();         // DI registration
///   2.  "ingest-parts" => await VerifiedPartsPlugin.RunCommandAsync(host, rest)  // CLI
///
/// Everything else is internal to this project.
///
/// REMOVE INSTRUCTIONS
/// ═══════════════════
///   1. Delete the NexusAgent.VerifiedParts/ folder.
///   2. Remove the &lt;ProjectReference&gt; from NexusAgent.Cli.csproj.
///   3. Remove the AddVerifiedParts() call from Program.cs.
///   4. Remove the "ingest-parts" switch case from Program.cs.
///   Done — NexusAgent.Core is untouched throughout.
/// </summary>
public static class VerifiedPartsPlugin
{
    // Default sinks (backward-compatible). Fossil only until landmark is explicitly requested.
    private static readonly string[] DefaultSinks = ["fossil"];

    // ── DI registration ───────────────────────────────────────────────────────

    public static IServiceCollection AddVerifiedParts(this IServiceCollection services)
    {
        services.AddSingleton<AxiomChecker>();
        // Idea 1 — fossil vault
        services.AddSingleton<IPartSink, FossilSink>();
        // Idea 2 — landmark/replay layer (freely combinable via --sinks fossil,landmark)
        services.AddSingleton<IPartSink, LandmarkSink>();
        services.AddSingleton<VerifiedPartIngestor>();
        return services;
    }

    // ── CLI command ───────────────────────────────────────────────────────────

    /// <summary>
    /// nexus ingest-parts --from-json &lt;path&gt; [--dry-run] [--native-decide flag|reject]
    ///                    [--sinks fossil,landmark] [--exclude-targets &lt;path&gt;]
    ///
    /// The JSON file must be an array of <see cref="VerifiedPart"/> objects.
    /// Each part is gate-checked (compile + axiom + scope) and, unless --dry-run, stored
    /// in each enabled sink.
    ///
    /// --sinks           Comma-separated list of sinks (default: fossil).
    ///                   Valid values: fossil, landmark.  Example: --sinks fossil,landmark
    /// --exclude-targets Path to a newline-separated list of fully-qualified Lean decl names
    ///                   to exclude from ingestion (holdout / eval-target enforcement).
    ///                   Exclusion is applied at the PARENT-PROBLEM level: any part whose
    ///                   parent problem id (e.g. erdos_1074) matches a target's parent id
    ///                   is excluded, not just exact-name matches.
    ///
    /// Exit codes: 0 = all ingested (or all passed dry-run), 1 = any rejected or error.
    /// </summary>
    public static async Task<int> RunCommandAsync(IHost host, string[] args)
    {
        var log      = host.Services.GetRequiredService<ILogger<VerifiedPartIngestor>>();
        var ingestor = host.Services.GetRequiredService<VerifiedPartIngestor>();

        var jsonPath       = GetFlag(args, "--from-json");
        var dryRun         = args.Contains("--dry-run");
        var nativeFlag     = GetFlag(args, "--native-decide");
        var sinksFlag      = GetFlag(args, "--sinks");
        var excludePath    = GetFlag(args, "--exclude-targets");

        if (string.IsNullOrWhiteSpace(jsonPath))
        {
            Console.Error.WriteLine("Usage: nexus ingest-parts --from-json <path> [options]");
            Console.Error.WriteLine("  --from-json         Path to a JSON array of VerifiedPart objects.");
            Console.Error.WriteLine("  --dry-run           Gate-check only; do not write to any sink.");
            Console.Error.WriteLine("  --native-decide     Override NEXUS_PARTS_NATIVE_DECIDE env var (reject|flag).");
            Console.Error.WriteLine("  --sinks             Comma-separated sinks (default: fossil). Values: fossil, landmark.");
            Console.Error.WriteLine("  --exclude-targets   Path to newline-separated eval target names (holdout filter).");
            return 1;
        }

        if (!string.IsNullOrWhiteSpace(nativeFlag))
            Environment.SetEnvironmentVariable("NEXUS_PARTS_NATIVE_DECIDE", nativeFlag);

        // ── parse active sinks ────────────────────────────────────────────────
        var activeSinkNames = string.IsNullOrWhiteSpace(sinksFlag)
            ? DefaultSinks
            : sinksFlag.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // ── load holdout exclusion set ────────────────────────────────────────
        HashSet<string> excludedNames      = [];
        HashSet<string> excludedParentIds  = [];
        if (!string.IsNullOrWhiteSpace(excludePath))
        {
            if (!File.Exists(excludePath))
            {
                log.LogError("--exclude-targets file not found: {Path}", excludePath);
                return 1;
            }
            excludedNames = [.. (await File.ReadAllLinesAsync(excludePath))
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#'))];
            // Parent-level exclusion: a sibling variant of an eval target is too close
            // to count as independent enrichment (it may be load-bearing in that target's
            // proof).  Derive the parent problem id from each excluded decl name so the
            // check covers the whole problem, not just the exact declaration.
            excludedParentIds = [.. excludedNames.Select(ExtractParentProblemId)];
            log.LogInformation(
                "Holdout filter: {N} excluded targets ({P} distinct parent problems) from {Path}",
                excludedNames.Count, excludedParentIds.Count, Path.GetFileName(excludePath));
        }

        // ── load parts ────────────────────────────────────────────────────────
        List<VerifiedPart> parts;
        try
        {
            var json = await File.ReadAllTextAsync(jsonPath);
            parts = JsonSerializer.Deserialize<List<VerifiedPart>>(json, JsonOptions)
                    ?? throw new InvalidDataException("JSON deserialized to null.");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to read parts from {Path}", jsonPath);
            return 1;
        }

        if (parts.Count == 0)
        {
            log.LogWarning("No parts found in {Path}", jsonPath);
            return 0;
        }

        Console.WriteLine($"[ingest-parts] {parts.Count} parts from {Path.GetFileName(jsonPath)}" +
                          $"  sinks=[{string.Join(",", activeSinkNames)}]" +
                          (dryRun ? "  (DRY RUN — no writes)" : ""));
        Console.WriteLine();

        // ── process ───────────────────────────────────────────────────────────
        var ingested = 0;
        var rejected = 0;
        var excluded = 0;
        var ct       = CancellationToken.None;

        foreach (var part in parts)
        {
            // Ingestion-time holdout enforcement — parent-problem level.
            // Exclude if the part IS the eval target (exact match) OR is a sibling variant
            // of one (same parent problem id).  A sibling may be load-bearing in the
            // target's proof and therefore cannot be treated as independent enrichment.
            var partParent = ExtractParentProblemId(part.ProblemId);
            if (excludedNames.Contains(part.PartName))
            {
                Console.WriteLine($"  EXCL  {part.PartName}  — exact holdout target, skipped");
                excluded++;
                continue;
            }
            if (excludedParentIds.Contains(partParent))
            {
                Console.WriteLine($"  EXCL  {part.PartName}  — sibling of holdout target (parent={partParent}), skipped");
                excluded++;
                continue;
            }

            if (dryRun)
            {
                var gate = await ingestor.DryRunAsync(part, ct);
                if (gate.Passed)
                {
                    Console.WriteLine($"  PASS  [{part.Scope}] {part.PartName}  axioms=[{string.Join(", ", gate.AxiomProfile)}]");
                    ingested++;
                }
                else
                {
                    Console.WriteLine($"  FAIL  {part.PartName}  — {gate.Reason}");
                    rejected++;
                }
            }
            else
            {
                var outcome = await ingestor.IngestAsync(part, activeSinkNames, ct);
                switch (outcome)
                {
                    case IngestOutcome.Ingested i:
                        var fossilPart = i.FossilId is not null ? $"  fossil={i.FossilId[..8]}" : "";
                        Console.WriteLine($"  OK    [{part.Scope}] {i.PartName}  sinks=[{string.Join(",", i.SinksWritten)}]{fossilPart}  axioms=[{string.Join(", ", i.AxiomProfile)}]");
                        ingested++;
                        break;
                    case IngestOutcome.Rejected r:
                        Console.WriteLine($"  SKIP  {r.PartName}  — {r.Reason}");
                        rejected++;
                        break;
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"[ingest-parts] Done: {ingested} {(dryRun ? "passed" : "ingested")}, " +
                          $"{rejected} rejected, {excluded} excluded (holdout).");

        return rejected > 0 ? 1 : 0;
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static string? GetFlag(string[] args, string flag)
    {
        var idx = Array.IndexOf(args, flag);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }

    /// <summary>
    /// Canonical problem-family key for holdout matching. Both eval-target decl names and
    /// ingested part identifiers pass through here, collapsing every name for one problem
    /// to a single comparable key.
    ///
    /// Rule: take the FIRST dot-segment — the top namespace, i.e. the problem identity —
    /// keep only [A-Za-z0-9], lowercase. First-segment-only is deliberate: it sidesteps
    /// PascalCase theorem names (<c>Reachable</c>, <c>KTExtendsK</c>) and guillemet segments
    /// (<c>«1308.0994»</c>) that broke the old "skip namespace / read next" rule, and it
    /// reconciles the two Erdős conventions (<c>Erdos1074</c> vs <c>erdos_1074</c>) after
    /// normalization.
    ///
    /// Examples:
    ///   <c>Erdos1074.erdos_1074.variants.EHSNumbers_init</c>  →  <c>erdos1074</c>
    ///   <c>erdos_1074.variants.mem_pillaiPrimes</c>           →  <c>erdos1074</c>
    ///   <c>Erdos26.not_isThick_of_finite</c>                  →  <c>erdos26</c>
    ///   <c>OpenQuantumProblem23.sicOverlapSq_three</c>        →  <c>openquantumproblem23</c>
    ///   <c>Arxiv.«1308.0994».KTExtendsK</c>                   →  <c>arxiv</c>  (coarsened)
    ///
    /// Conservative coarsening (over-exclude is the safe error for holdout): multiple
    /// sub-conjectures under a single top namespace (e.g. WrittenOnTheWallII.*) collapse to
    /// one key. The exact-decl check still runs first. Use <see cref="VerifiedPart.ProblemId"/>
    /// for a more specific token when coarsening loses real enrichment.
    /// </summary>
    internal static string ExtractParentProblemId(string declName)
    {
        if (string.IsNullOrWhiteSpace(declName)) return string.Empty;
        var firstSegment = declName.Split('.', 2)[0];
        return new string(firstSegment.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters                  = { new JsonStringEnumConverter() },
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
    };
}
