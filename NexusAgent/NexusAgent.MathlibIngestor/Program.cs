using NexusAgent.MathlibIngestor.Processing;

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    PrintUsage();
    return 1;
}

var input = GetArg("--input");
var output = GetArg("--out") ?? Path.Combine(Environment.CurrentDirectory, "out", "mathlib-csv");
var limit = ParseIntArg("--limit");

if (string.IsNullOrWhiteSpace(input) || !File.Exists(input))
{
    Console.Error.WriteLine("Missing or invalid --input path.");
    PrintUsage();
    return 1;
}

Console.WriteLine($"Input: {input}");
Console.WriteLine($"Output: {output}");
if (limit.HasValue) Console.WriteLine($"Limit: {limit.Value:N0} rows");

Directory.CreateDirectory(output);

var parsed = 0;
var transitions = 0;
using var sink = new CsvSink(output);
foreach (var r in LeanDojoParser.Parse(input, limit))
{
    var beforeShape = GoalCanonicalizer.Canonicalize(r.GoalBefore);
    var afterShapes = r.GoalsAfter
        .Select(GoalCanonicalizer.Canonicalize)
        .ToArray();

    // For successful terminal transitions with no residual goals, route to solved node.
    if (afterShapes.Length == 0 && r.Success)
    {
        afterShapes = [GoalCanonicalizer.Canonicalize("no goals")];
    }

    sink.RecordTransition(
        beforeShape,
        afterShapes,
        r.TacticRaw,
        theoremSource: r.TheoremName,
        moduleSource: r.ModuleSource ?? string.Empty);

    parsed++;
    transitions += Math.Max(afterShapes.Length, 1);
    if (parsed % 10_000 == 0) Console.WriteLine($"Parsed {parsed:N0} rows...");
}

sink.Flush();
WriteImportScripts(output);

Console.WriteLine($"Done. Parsed {parsed:N0} rows.");
Console.WriteLine($"Transitions exported: {transitions:N0}");
Console.WriteLine("Generated files: goals_nodes.csv, tactics_nodes.csv, edges.csv, import_neo4j.sh, validate_graph.cypher");
return 0;

string? GetArg(string key)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (!string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase)) continue;
        return args[i + 1];
    }
    return null;
}

int? ParseIntArg(string key)
{
    var raw = GetArg(key);
    if (string.IsNullOrWhiteSpace(raw)) return null;
    return int.TryParse(raw, out var n) ? n : null;
}

void PrintUsage()
{
    Console.WriteLine("Mathlib graph extractor (InfoTree/LeanDojo JSON/JSONL -> Neo4j bulk CSVs)");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project NexusAgent.MathlibIngestor -- \\");
    Console.WriteLine("    --input /path/to/lean-dojo.jsonl \\");
    Console.WriteLine("    --out /path/to/output/dir \\");
    Console.WriteLine("    --limit 10000");
}

void WriteImportScripts(string outputDir)
{
    var importSh = Path.Combine(outputDir, "import_neo4j.sh");
    var validateCypher = Path.Combine(outputDir, "validate_graph.cypher");

    File.WriteAllText(importSh,
        "#!/usr/bin/env bash\n" +
        "set -euo pipefail\n\n" +
        "if [[ $# -lt 2 ]]; then\n" +
        "  echo \"Usage: $0 <neo4j-home> <database-name>\"\n" +
        "  exit 1\n" +
        "fi\n\n" +
        "NEO4J_HOME=\"$1\"\n" +
        "DB_NAME=\"$2\"\n" +
        "CSV_DIR=\"$(cd \"$(dirname \"${BASH_SOURCE[0]}\")\" && pwd)\"\n\n" +
        "echo \"Importing graph into ${DB_NAME} from ${CSV_DIR}\"\n" +
        "${NEO4J_HOME}/bin/neo4j-admin database import full ${DB_NAME} \\\n" +
        "  --nodes=GoalShape=${CSV_DIR}/goals_nodes.csv \\\n" +
        "  --nodes=TacticApplication=${CSV_DIR}/tactics_nodes.csv \\\n" +
        "  --relationships=${CSV_DIR}/edges.csv\n\n" +
        "echo \"Done. Start Neo4j and run validate_graph.cypher\"\n");

    File.WriteAllText(validateCypher,
        "MATCH (g:GoalShape) RETURN count(g) AS goal_nodes;\n" +
        "MATCH (t:TacticApplication) RETURN count(t) AS tactic_nodes;\n" +
        "MATCH ()-[r:PROPOSED_MOVE]->() RETURN count(r) AS proposed_edges, sum(r.frequency) AS total_frequency;\n" +
        "MATCH ()-[r:YIELDS]->() RETURN count(r) AS yields_edges;\n" +
        "MATCH (g:GoalShape {is_solved: true}) RETURN count(g) AS solved_nodes;\n" +
        "MATCH (g:GoalShape)-[:PROPOSED_MOVE]->(t:TacticApplication)-[:YIELDS]->(n:GoalShape)\n" +
        "RETURN g.hash AS parent, t.tactic_raw AS tactic, collect(n.hash)[0..5] AS sample_children LIMIT 10;\n");
}
