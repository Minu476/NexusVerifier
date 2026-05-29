using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NexusAgent.Core.Memory;

/// <summary>
/// Reads the JSONL file written by the Lean ErdosHypergraph engine
/// (<c>_nexus_tmp/hg_cache.jsonl</c>) and upserts each edge into Neo4j
/// as a <c>:HyperedgeRecord</c> node.
///
/// <para>
/// Two cache files are written on a cold Lean run:
/// <list type="bullet">
///   <item><c>hg_cache.hge</c> — tab-separated, read by Lean warm-start (fast reload)</item>
///   <item><c>hg_cache.jsonl</c> — JSON lines, read by this ingestor → Neo4j</item>
/// </list>
/// </para>
///
/// <para>Pipeline:</para>
/// <code>
///   Lean cold run → hg_cache.hge   (Lean warm-start, ~0.2s)
///                → hg_cache.jsonl → HyperedgeIngestor → Neo4j :HyperedgeRecord
///                                                              ↓
///                                                   C# agent Cypher queries
/// </code>
/// </summary>
public sealed class HyperedgeIngestor
{
    private readonly INeo4jClient _neo4j;
    private readonly ILogger<HyperedgeIngestor> _log;

    public HyperedgeIngestor(INeo4jClient neo4j, ILogger<HyperedgeIngestor> log)
    {
        _neo4j = neo4j;
        _log = log;
    }

    /// <summary>
    /// Read <paramref name="jsonlPath"/>, parse each line as a hyperedge,
    /// and upsert into Neo4j. Returns the number of edges ingested.
    /// </summary>
    public async Task<int> IngestAsync(string jsonlPath, CancellationToken ct)
    {
        if (!File.Exists(jsonlPath))
            throw new FileNotFoundException($"Hyperedge JSONL not found: {jsonlPath}");

        var lines = await File.ReadAllLinesAsync(jsonlPath, ct);
        var seedRun = Guid.NewGuid().ToString("N")[..8];
        var builtAt = DateTime.UtcNow;

        var edges = new List<HyperedgeRecord>();
        var skipped = 0;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            var edge = ParseLine(trimmed, builtAt, seedRun);
            if (edge is null) { skipped++; continue; }
            edges.Add(edge);
        }

        _log.LogInformation(
            "Parsed {Count} edges from {File} ({Skipped} lines skipped)",
            edges.Count, jsonlPath, skipped);

        await _neo4j.UpsertHyperedgesAsync(edges, ct);

        _log.LogInformation("Upserted {Count} :HyperedgeRecord nodes into Neo4j", edges.Count);
        return edges.Count;
    }

    /// <summary>
    /// Dump all Neo4j-stored edges back to a JSONL file so the Lean engine
    /// can do a warm-start without re-running <c>buildHypergraph</c>.
    /// Useful when the JSONL cache file has been deleted or the machine changed.
    /// </summary>
    public async Task ExportToJsonlAsync(string outputPath, CancellationToken ct)
    {
        var edges = await _neo4j.GetAllHyperedgesAsync(ct);
        var lines = edges.Select(SerializeLine);
        await File.WriteAllLinesAsync(outputPath, lines, ct);
        _log.LogInformation("Exported {Count} edges to {Path}", edges.Count, outputPath);
    }

    // ── Serialization helpers ────────────────────────────────────────────────

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Serialize one edge as a compact JSON line matching the format written by
    /// <c>Hypergraph.toJSONL</c> in the Lean engine.
    /// Format: <c>{"fn":"...","inputs":[...],"output":"..."}</c>
    /// </summary>
    private static string SerializeLine(HyperedgeRecord e)
    {
        var obj = new { fn = e.LemmaName, inputs = e.Inputs, output = e.Output };
        return JsonSerializer.Serialize(obj, _jsonOpts);
    }

    /// <summary>
    /// Parse one JSONL line.  Expected format (from <c>Hypergraph.toJSONL</c>):
    /// <c>{"fn":"Nat.add_comm","inputs":[],"output":"n + m = m + n"}</c>
    /// </summary>
    private static HyperedgeRecord? ParseLine(string line, DateTime builtAt, string seedRun)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            var fn = root.GetProperty("fn").GetString() ?? "";
            var output = root.GetProperty("output").GetString() ?? "";
            var inputs = root.GetProperty("inputs")
                             .EnumerateArray()
                             .Select(e => e.GetString() ?? "")
                             .ToArray();

            if (string.IsNullOrEmpty(fn) || string.IsNullOrEmpty(output))
                return null;

            // Stable ID: SHA256 of "lemmaName:output" — matches across runs.
            var idBytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{fn}:{output}"));
            var id = Convert.ToHexString(idBytes)[..16].ToLowerInvariant();

            // Reproduce the Lean HashMap hash(output) — Lean uses FNV-like UInt64 hash.
            // We approximate with GetHashCode for index alignment; exact match not required
            // since the C# agent queries by lemmaName/output text, not by hash bucket.
            var outputHash = (ulong)(uint)output.GetHashCode();

            return new HyperedgeRecord
            {
                Id         = id,
                LemmaName  = fn,
                Output     = output,
                OutputHash = outputHash,
                Inputs     = inputs,
                BuiltAt    = builtAt,
                SeedRun    = seedRun,
            };
        }
        catch
        {
            return null;
        }
    }
}
