using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;

namespace NexusAgent.MathlibIngestor.Processing;

internal static partial class GoalCanonicalizer
{
    private static readonly Regex MultiSpace = new("\\s+", RegexOptions.Compiled);
    private static readonly Regex Identifier = new(@"\b[A-Za-z_][A-Za-z0-9_']*\b", RegexOptions.Compiled);
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "forall", "fun", "by", "let", "have", "show", "from", "if", "then", "else",
        "match", "with", "Type", "Sort", "Prop", "True", "False", "Nat", "Int", "Rat",
        "Real", "Complex", "And", "Or", "Not", "Exists"
    };

    public static CanonicalGoalShape Canonicalize(string rawGoal)
    {
        if (string.IsNullOrWhiteSpace(rawGoal))
        {
            return BuildSolvedShape();
        }

        var trimmed = rawGoal.Trim();
        if (IsSolved(trimmed)) return BuildSolvedShape();

        var normalized = NormalizeGoal(trimmed);
        return new CanonicalGoalShape
        {
            Hash = HashGoal(normalized),
            CanonicalText = normalized,
            IsSolved = false,
        };
    }

    private static string NormalizeGoal(string goal)
    {
        var g = goal.Replace("\r\n", "\n").Replace('\r', '\n');

        var parts = g.Split('⊢');
        var lhs = parts.Length > 1 ? parts[0] : string.Empty;
        var rhs = parts.Length > 1 ? parts[1] : parts[0];

        var normalizedHyps = NormalizeHypotheses(lhs);
        var normalizedTarget = NormalizeExpr(rhs);

        if (normalizedHyps.Length == 0)
        {
            return $"⊢ {normalizedTarget}";
        }

        return $"{normalizedHyps} ⊢ {normalizedTarget}";
    }

    private static string NormalizeHypotheses(string lhs)
    {
        if (string.IsNullOrWhiteSpace(lhs)) return string.Empty;

        var hypothesisRows = lhs
            .Split(['\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeExpr)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        return string.Join(" ; ", hypothesisRows);
    }

    private static string NormalizeExpr(string expr)
    {
        var slotMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var slotCounter = 0;

        var replaced = Identifier.Replace(expr, m =>
        {
            var token = m.Value;
            if (Reserved.Contains(token)) return token;
            if (char.IsUpper(token[0])) return token; // keep type/constant heads stable

            if (!slotMap.TryGetValue(token, out var slot))
            {
                slot = $"slot_{slotCounter++}";
                slotMap[token] = slot;
            }
            return slot;
        });

        return MultiSpace.Replace(replaced.Trim(), " ");
    }

    private static bool IsSolved(string goal)
    {
        var g = goal.Trim().ToLowerInvariant();
        return g.Length == 0 || g == "no goals" || g == "solved";
    }

    private static CanonicalGoalShape BuildSolvedShape()
    {
        const string solved = "⊢ SolvedState";
        return new CanonicalGoalShape
        {
            Hash = HashGoal(solved),
            CanonicalText = solved,
            IsSolved = true,
        };
    }

    private static string HashGoal(string canonicalText)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalText));
        return $"g_{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }
}

internal sealed record CanonicalGoalShape
{
    public required string Hash { get; init; }
    public required string CanonicalText { get; init; }
    public required bool IsSolved { get; init; }
}
