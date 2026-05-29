using System.Text.Json;
using NexusAgent.MathlibIngestor.Models;

namespace NexusAgent.MathlibIngestor.Processing;

internal static class LeanDojoParser
{
    public static IEnumerable<IngestRecord> Parse(string inputPath, int? limit)
    {
        var ext = Path.GetExtension(inputPath).ToLowerInvariant();
        return ext switch
        {
            ".jsonl" => ParseJsonLines(inputPath, limit),
            ".json" => ParseJsonArray(inputPath, limit),
            _ => throw new InvalidOperationException($"Unsupported input type: {ext}. Use .json or .jsonl")
        };
    }

    private static IEnumerable<IngestRecord> ParseJsonLines(string inputPath, int? limit)
    {
        var seen = 0;
        foreach (var line in File.ReadLines(inputPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            if (TryMap(doc.RootElement, out var record))
            {
                yield return record;
                seen++;
                if (limit.HasValue && seen >= limit.Value) yield break;
            }
        }
    }

    private static IEnumerable<IngestRecord> ParseJsonArray(string inputPath, int? limit)
    {
        using var stream = File.OpenRead(inputPath);
        using var doc = JsonDocument.Parse(stream);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Expected top-level JSON array for .json input.");

        var seen = 0;
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            // LeanDojo Benchmark 4 stores theorem-level objects with traced_tactics.
            if (TryMapTracedTactics(row, out var tracedRecords))
            {
                foreach (var tracedRecord in tracedRecords)
                {
                    yield return tracedRecord;
                    seen++;
                    if (limit.HasValue && seen >= limit.Value) yield break;
                }
                continue;
            }

            if (TryMap(row, out var record))
            {
                yield return record;
                seen++;
                if (limit.HasValue && seen >= limit.Value) yield break;
            }
        }
    }

    private static bool TryMap(JsonElement row, out IngestRecord record)
    {
        // Preferred path: InfoTree transition shape (goals_after as array).
        if (TryMapInfoTreeTransition(row, out record))
        {
            return true;
        }

        var theoremName = GetString(row, "theorem", "theorem_name", "decl_name", "name");
        var theoremNs = GetString(row, "namespace", "theorem_namespace");
        var theoremStmt = GetString(row, "statement", "theorem_statement", "decl_type");
        var moduleSource = GetString(row, "module", "module_source", "file", "source_file");

        var goalBefore = GetString(row, "tactic_state_before", "state_before", "goal_before");
        var goalAfter = GetString(row, "tactic_state_after", "state_after", "goal_after");
        var tactic = GetString(row, "tactic", "tactic_raw", "tactic_applied", "action");

        if (string.IsNullOrWhiteSpace(theoremName)
            || string.IsNullOrWhiteSpace(goalBefore)
            || string.IsNullOrWhiteSpace(tactic))
        {
            record = default!;
            return false;
        }

        var premises = GetStringArray(row, "premises_used", "premises", "used_premises");
        var success = GetBool(row, "success", "is_success") ?? !string.IsNullOrWhiteSpace(goalAfter);
        var goalsAfter = string.IsNullOrWhiteSpace(goalAfter)
            ? Array.Empty<string>()
            : new[] { goalAfter };

        record = new IngestRecord(
            moduleSource,
            theoremName,
            theoremNs,
            theoremStmt,
            goalBefore,
            goalsAfter,
            tactic,
            premises,
            success);
        return true;
    }

    private static bool TryMapInfoTreeTransition(JsonElement row, out IngestRecord record)
    {
        var theoremName = GetString(row, "theorem_source", "theorem_name", "theorem", "decl_name", "name");
        var goalBefore = GetString(row, "goal_before", "state_before", "tactic_state_before");
        var tacticRaw = GetString(row, "tactic_raw", "tactic", "action", "tactic_applied");
        var goalsAfter = GetStringArray(row, "goals_after", "states_after", "goal_after_list");

        if (string.IsNullOrWhiteSpace(theoremName)
            || string.IsNullOrWhiteSpace(goalBefore)
            || string.IsNullOrWhiteSpace(tacticRaw))
        {
            record = default!;
            return false;
        }

        var theoremNs = GetString(row, "theorem_namespace", "namespace");
        var theoremStmt = GetString(row, "theorem_statement", "statement", "decl_type");
        var moduleSource = GetString(row, "module_source", "module", "file", "source_file");
        var premises = GetStringArray(row, "premises", "premises_used", "used_premises");
        var success = GetBool(row, "success", "is_success") ?? goalsAfter.Count == 0;

        record = new IngestRecord(
            moduleSource,
            theoremName,
            theoremNs,
            theoremStmt,
            goalBefore,
            goalsAfter,
            tacticRaw,
            premises,
            success);
        return true;
    }

    private static bool TryMapTracedTactics(JsonElement row, out List<IngestRecord> records)
    {
        records = new List<IngestRecord>();

        var theoremName = GetString(row, "full_name", "theorem", "theorem_name", "decl_name", "name");
        if (string.IsNullOrWhiteSpace(theoremName)) return false;
        if (!row.TryGetProperty("traced_tactics", out var tracedTactics)) return false;
        if (tracedTactics.ValueKind != JsonValueKind.Array) return false;

        var theoremNs = GetString(row, "namespace", "theorem_namespace");
        var theoremStmt = GetString(row, "statement", "theorem_statement", "decl_type");
            var moduleSource = GetString(row, "module", "module_source", "file", "source_file");

        foreach (var t in tracedTactics.EnumerateArray())
        {
            var goalBefore = GetString(t, "state_before", "tactic_state_before", "goal_before");
            var goalAfter = GetString(t, "state_after", "tactic_state_after", "goal_after") ?? string.Empty;
            var tactic = GetString(t, "tactic", "tactic_raw", "tactic_applied", "action");
            if (string.IsNullOrWhiteSpace(goalBefore) || string.IsNullOrWhiteSpace(tactic)) continue;

            var premises = GetPremisesFromAnnotatedTactic(t);
            var success = !string.IsNullOrWhiteSpace(goalAfter);
                var goalsAfter = string.IsNullOrWhiteSpace(goalAfter)
                    ? Array.Empty<string>()
                    : new[] { goalAfter };

            records.Add(new IngestRecord(
                    moduleSource,
                theoremName,
                theoremNs,
                theoremStmt,
                goalBefore,
                    goalsAfter,
                tactic,
                premises,
                success));
        }

        return records.Count > 0;
    }

    private static string? GetString(JsonElement row, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }
        return null;
    }

    private static bool? GetBool(JsonElement row, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!row.TryGetProperty(key, out var value)) continue;
            if (value.ValueKind == JsonValueKind.True) return true;
            if (value.ValueKind == JsonValueKind.False) return false;
        }
        return null;
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement row, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!row.TryGetProperty(key, out var value)) continue;
            if (value.ValueKind != JsonValueKind.Array) continue;
            var items = new List<string>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) items.Add(s);
                }
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    var s = GetString(item,
                        "goal", "goal_text", "state", "state_text",
                        "full_name", "name", "decl_name");
                    if (!string.IsNullOrWhiteSpace(s)) items.Add(s);
                }
            }
            return items;
        }
        return Array.Empty<string>();
    }

    private static IReadOnlyList<string> GetPremisesFromAnnotatedTactic(JsonElement row)
    {
        if (!row.TryGetProperty("annotated_tactic", out var annotated))
            return Array.Empty<string>();
        if (annotated.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        if (annotated.GetArrayLength() < 2) return Array.Empty<string>();

        using var enumerator = annotated.EnumerateArray();
        _ = enumerator.MoveNext(); // tactic text
        if (!enumerator.MoveNext()) return Array.Empty<string>();

        var second = enumerator.Current;
        if (second.ValueKind != JsonValueKind.Array) return Array.Empty<string>();

        var premises = new List<string>();
        foreach (var item in second.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s)) premises.Add(s);
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                var name = GetString(item, "full_name", "name", "decl_name");
                if (!string.IsNullOrWhiteSpace(name)) premises.Add(name);
            }
        }

        return premises;
    }
}
