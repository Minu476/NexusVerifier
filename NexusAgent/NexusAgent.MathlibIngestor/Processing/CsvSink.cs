using System.Security.Cryptography;
using System.Text;

namespace NexusAgent.MathlibIngestor.Processing;

internal sealed class CsvSink : IDisposable
{
    private readonly StreamWriter _goalsNodes;
    private readonly StreamWriter _tacticsNodes;
    private readonly StreamWriter _edges;

    private readonly Dictionary<string, CanonicalGoalShape> _goals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TacticNode> _tactics = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _proposedMoveFrequency = new(StringComparer.Ordinal);
    private readonly HashSet<string> _yieldsEdges = new(StringComparer.Ordinal);

    public CsvSink(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        _goalsNodes = CreateWriter(Path.Combine(outputDir, "goals_nodes.csv"));
        _tacticsNodes = CreateWriter(Path.Combine(outputDir, "tactics_nodes.csv"));
        _edges = CreateWriter(Path.Combine(outputDir, "edges.csv"));

        _goalsNodes.WriteLine("hash:ID,canonical_text,is_solved:boolean,:LABEL");
        _tacticsNodes.WriteLine("tacticId:ID,tactic_raw,theorem_source,module_source,:LABEL");
        _edges.WriteLine(":START_ID,:END_ID,frequency:int,branch_index:int,:TYPE");
    }

    public void RecordTransition(
        CanonicalGoalShape goalBefore,
        IReadOnlyList<CanonicalGoalShape> goalsAfter,
        string tacticRaw,
        string theoremSource,
        string moduleSource)
    {
        _goals[goalBefore.Hash] = goalBefore;
        foreach (var g in goalsAfter) _goals[g.Hash] = g;

        var tacticId = BuildTacticNodeId(goalBefore.Hash, tacticRaw);
        if (!_tactics.ContainsKey(tacticId))
        {
            _tactics[tacticId] = new TacticNode
            {
                TacticId = tacticId,
                TacticRaw = tacticRaw,
                TheoremSource = theoremSource,
                ModuleSource = moduleSource,
            };
        }

        var proposedKey = $"{goalBefore.Hash}|{tacticId}";
        if (!_proposedMoveFrequency.TryGetValue(proposedKey, out var frequency)) frequency = 0;
        _proposedMoveFrequency[proposedKey] = frequency + 1;

        for (var i = 0; i < goalsAfter.Count; i++)
        {
            var yieldsKey = $"{tacticId}|{goalsAfter[i].Hash}|{i}";
            _yieldsEdges.Add(yieldsKey);
        }
    }

    public void Flush()
    {
        foreach (var g in _goals.Values.OrderBy(g => g.Hash, StringComparer.Ordinal))
        {
            _goalsNodes.WriteLine(string.Join(',',
                Csv(g.Hash),
                Csv(g.CanonicalText),
                Csv(g.IsSolved ? "true" : "false"),
                Csv("GoalShape")));
        }

        foreach (var t in _tactics.Values.OrderBy(t => t.TacticId, StringComparer.Ordinal))
        {
            _tacticsNodes.WriteLine(string.Join(',',
                Csv(t.TacticId),
                Csv(t.TacticRaw),
                Csv(t.TheoremSource),
                Csv(t.ModuleSource),
                Csv("TacticApplication")));
        }

        foreach (var kv in _proposedMoveFrequency.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var parts = kv.Key.Split('|');
            var from = parts[0];
            var to = parts[1];
            _edges.WriteLine(string.Join(',',
                Csv(from),
                Csv(to),
                kv.Value,
                Csv(string.Empty),
                Csv("PROPOSED_MOVE")));
        }

        foreach (var key in _yieldsEdges.OrderBy(k => k, StringComparer.Ordinal))
        {
            var parts = key.Split('|');
            var from = parts[0];
            var to = parts[1];
            var branch = int.Parse(parts[2]);
            _edges.WriteLine(string.Join(',',
                Csv(from),
                Csv(to),
                Csv(string.Empty),
                branch,
                Csv("YIELDS")));
        }

        _goalsNodes.Flush();
        _tacticsNodes.Flush();
        _edges.Flush();
    }

    public void Dispose()
    {
        _goalsNodes.Dispose();
        _tacticsNodes.Dispose();
        _edges.Dispose();
    }

    private static string BuildTacticNodeId(string parentHash, string tacticRaw)
    {
        var key = $"{parentHash}|{tacticRaw.Trim()}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return $"t_{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }

    private static StreamWriter CreateWriter(string path)
        => new(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private static string Csv(string value)
    {
        var escaped = value.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }

    private sealed record TacticNode
    {
        public required string TacticId { get; init; }
        public required string TacticRaw { get; init; }
        public required string TheoremSource { get; init; }
        public required string ModuleSource { get; init; }
    }
}
