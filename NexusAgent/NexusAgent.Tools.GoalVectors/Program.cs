using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NexusAgent.Core.Configuration;
using NexusAgent.Core.Encoding;
using NexusAgent.Core.Models;

var opts = ParseArgs(args);
if (opts is null)
{
    PrintUsage();
    return 1;
}

Directory.CreateDirectory(Path.GetDirectoryName(opts.GoalVectorsOutPath)!);
Directory.CreateDirectory(Path.GetDirectoryName(opts.ErdosVectorsOutPath)!);

var cfg = new NexusConfig
{
    TacticVocabPath = opts.TacticVocabPath
};
var encoder = new ProofStateEncoder(Options.Create(cfg), NullLogger<ProofStateEncoder>.Instance);

Console.WriteLine($"Corpus: {opts.CorpusJsonlPath}");
Console.WriteLine($"Goal vectors out: {opts.GoalVectorsOutPath}");
Console.WriteLine($"Erdos dir: {opts.ErdosLeanDir}");
Console.WriteLine($"Erdos vectors out: {opts.ErdosVectorsOutPath}");

var goalRows = BuildGoalVectors(opts.CorpusJsonlPath, encoder, opts.DomainTag);
WriteCsv(opts.GoalVectorsOutPath, "hash,vector,canonical_goal", goalRows.Select(r => new[] { r.Hash, r.Vector, r.Goal }));
Console.WriteLine($"Wrote goal vectors: {goalRows.Count:N0}");

var erdosRows = BuildErdosVectors(opts.ErdosLeanDir, encoder, opts.DomainTag);
WriteCsv(opts.ErdosVectorsOutPath, "problem_id,theorem_name,goal_text,vector", erdosRows.Select(r => new[] { r.ProblemId, r.TheoremName, r.GoalText, r.Vector }));
Console.WriteLine($"Wrote Erdos vectors: {erdosRows.Count:N0}");

return 0;

static List<GoalVectorRow> BuildGoalVectors(string corpusPath, ProofStateEncoder encoder, string domainTag)
{
    if (!File.Exists(corpusPath)) throw new FileNotFoundException("Corpus JSONL not found", corpusPath);

    var byHash = new Dictionary<string, string>(StringComparer.Ordinal);
    var seen = 0;

    foreach (var row in EnumerateTheoremRows(corpusPath))
    {
        if (!row.TryGetProperty("traced_tactics", out var traced) || traced.ValueKind != JsonValueKind.Array)
            continue;

        foreach (var t in traced.EnumerateArray())
        {
            var goalBefore = GetString(t, "state_before", "tactic_state_before", "goal_before");
            if (string.IsNullOrWhiteSpace(goalBefore)) continue;

            var canonical = CanonicalizeGoal(goalBefore);
            if (canonical.Length == 0) continue;

            var hash = Sha256Hex(canonical);
            if (!byHash.ContainsKey(hash)) byHash[hash] = canonical;
            seen++;
        }

        if (seen > 0 && seen % 1_000_000 == 0) Console.WriteLine($"Scanned tactics: {seen:N0}");
    }

    var rows = new List<GoalVectorRow>(byHash.Count);
    foreach (var (hash, goal) in byHash)
    {
        var vec = EncodeGoalText(encoder, goal, domainTag);
        rows.Add(new GoalVectorRow(hash, vec, goal));
    }

    rows.Sort((a, b) => string.CompareOrdinal(a.Hash, b.Hash));
    return rows;
}

static IEnumerable<JsonElement> EnumerateTheoremRows(string path)
{
    var ext = Path.GetExtension(path).ToLowerInvariant();
    if (ext == ".jsonl")
    {
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            yield return doc.RootElement.Clone();
        }
        yield break;
    }

    if (ext == ".json")
    {
        using var fs = File.OpenRead(path);
        using var doc = JsonDocument.Parse(fs);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Expected top-level JSON array for .json corpus file.");

        foreach (var item in doc.RootElement.EnumerateArray())
            yield return item.Clone();
        yield break;
    }

    throw new InvalidOperationException($"Unsupported corpus extension '{ext}'. Use .json or .jsonl.");
}

static List<ErdosVectorRow> BuildErdosVectors(string erdosDir, ProofStateEncoder encoder, string domainTag)
{
    if (!Directory.Exists(erdosDir)) throw new DirectoryNotFoundException($"Erdos directory not found: {erdosDir}");

    var files = Directory.GetFiles(erdosDir, "*.lean", SearchOption.TopDirectoryOnly)
        .OrderBy(f => f, StringComparer.Ordinal)
        .ToArray();

    var rows = new List<ErdosVectorRow>(files.Length);
    foreach (var file in files)
    {
        var text = File.ReadAllText(file);
        var parsed = ExtractTheoremStatement(text);
        if (parsed is null) continue;

        var problemId = $"Erdos_{Path.GetFileNameWithoutExtension(file)}";
        var goalText = CanonicalizeGoal(parsed.Statement);
        var vec = EncodeGoalText(encoder, goalText, domainTag);
        rows.Add(new ErdosVectorRow(problemId, parsed.Name, goalText, vec));
    }

    return rows;
}

static string EncodeGoalText(ProofStateEncoder encoder, string goalText, string domainTag)
{
    var state = new ProofState
    {
        PendingGoals = [goalText],
        Hypotheses = [],
        TacticHistory = [],
        SorryCount = 0,
        ErrorMessages = [],
        DomainTag = domainTag,
        SketchHash = string.Empty,
    };

    var v = encoder.Encode(state);
    return string.Join('|', v.Select(x => x.ToString("G9", CultureInfo.InvariantCulture)));
}

static void WriteCsv(string path, string header, IEnumerable<string[]> rows)
{
    using var w = new StreamWriter(path, false, new UTF8Encoding(false));
    w.WriteLine(header);
    foreach (var row in rows)
    {
        w.WriteLine(string.Join(',', row.Select(Csv)));
    }
}

static string Csv(string value)
{
    var escaped = value.Replace("\"", "\"\"");
    return $"\"{escaped}\"";
}

static string CanonicalizeGoal(string rawGoal)
{
    if (string.IsNullOrWhiteSpace(rawGoal)) return string.Empty;
    var normalized = Regex.Replace(rawGoal.Trim(), "\\s+", " ");
    normalized = Regex.Replace(normalized, @"\\bforall\\s+[a-zA-Z][a-zA-Z0-9_']*", "forall _");
    normalized = Regex.Replace(normalized, @"\\b∃\\s+[a-zA-Z][a-zA-Z0-9_']*", "∃ _");
    return normalized;
}

static string Sha256Hex(string value)
{
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
    var sb = new StringBuilder(bytes.Length * 2);
    foreach (var b in bytes) sb.Append(b.ToString("x2"));
    return sb.ToString();
}

static string? GetString(JsonElement row, params string[] keys)
{
    foreach (var key in keys)
    {
        if (row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
            return value.GetString();
    }
    return null;
}

static TheoremStatement? ExtractTheoremStatement(string text)
{
    var lines = text.Split('\n');
    for (var i = 0; i < lines.Length; i++)
    {
        var m = Regex.Match(lines[i], @"^\s*theorem\s+([A-Za-z0-9_'.]+)\s*:(.*)$");
        if (!m.Success) continue;

        var name = m.Groups[1].Value;
        var sb = new StringBuilder();
        sb.Append(m.Groups[2].Value);

        for (var j = i + 1; j < lines.Length; j++)
        {
            sb.Append(' ');
            sb.Append(lines[j]);
            if (lines[j].Contains(":= by", StringComparison.Ordinal))
                break;
        }

        var combined = sb.ToString();
        var idx = combined.IndexOf(":= by", StringComparison.Ordinal);
        if (idx >= 0) combined = combined[..idx];
        combined = combined.Trim();
        return combined.Length == 0 ? null : new TheoremStatement(name, combined);
    }

    return null;
}

static ToolOptions? ParseArgs(string[] args)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length - 1; i += 2)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
        d[args[i]] = args[i + 1];
    }

    if (!d.TryGetValue("--corpus-jsonl", out var corpus)) return null;
    if (!d.TryGetValue("--goal-vectors-out", out var goalOut)) return null;
    if (!d.TryGetValue("--erdos-dir", out var erdosDir)) return null;
    if (!d.TryGetValue("--erdos-vectors-out", out var erdosOut)) return null;

    var tacticVocab = d.TryGetValue("--tactic-vocab", out var tv)
        ? tv
        : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "tactics_vocab.json"));

    var domain = d.TryGetValue("--domain", out var dom) && !string.IsNullOrWhiteSpace(dom)
        ? dom
        : "other";

    return new ToolOptions(
        Path.GetFullPath(corpus),
        Path.GetFullPath(goalOut),
        Path.GetFullPath(erdosDir),
        Path.GetFullPath(erdosOut),
        Path.GetFullPath(tacticVocab),
        domain);
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project NexusAgent.Tools.GoalVectors -- \\");
    Console.WriteLine("    --corpus-jsonl /path/to/corpus.jsonl \\");
    Console.WriteLine("    --goal-vectors-out /path/to/goalshape_vectors.csv \\");
    Console.WriteLine("    --erdos-dir /path/to/erdos_phase9_ams5 \\");
    Console.WriteLine("    --erdos-vectors-out /path/to/erdos_vectors.csv \\");
    Console.WriteLine("    [--tactic-vocab /path/to/tactics_vocab.json] \\");
    Console.WriteLine("    [--domain other]");
}

internal sealed record GoalVectorRow(string Hash, string Vector, string Goal);
internal sealed record ErdosVectorRow(string ProblemId, string TheoremName, string GoalText, string Vector);
internal sealed record TheoremStatement(string Name, string Statement);
internal sealed record ToolOptions(
    string CorpusJsonlPath,
    string GoalVectorsOutPath,
    string ErdosLeanDir,
    string ErdosVectorsOutPath,
    string TacticVocabPath,
    string DomainTag);
