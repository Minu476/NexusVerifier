using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexusAgent.Core.Configuration;
using NexusAgent.Core.Models;

namespace NexusAgent.Core.Encoding;

/// <summary>
/// Encodes a Lean ProofState into a 64-dim L2-normalized float vector.
///
/// Layout (see SPEC.md §4.1):
///   [ 0.. 7]  Tactic bag (TF over 200-entry Mathlib tactic vocab, 8 buckets)
///   [ 8..31]  Goal-shape fingerprint (hashed token bigrams of pending goals, 24 buckets)
///   [32..47]  Hypothesis fingerprint (sorted hashes, top-16)
///   [48..55]  Last-4 tactic n-grams (hashed 2-grams)
///   [56..59]  Scalars: sorry_count, error_count, goal_depth, hypothesis_count
///   [60..63]  Domain one-hot (combinatorics/algebra/analysis/other)
///
/// NOTE: goal-shape was widened from 16→24 buckets (tactic-bag shrunk 16→8) to
/// reduce cosine-similarity false-positives from hash collisions on goal bigrams.
///
/// This is intentionally deterministic and interpretable — no neural embedding.
/// Drop-in replacement with a 512-dim model is straightforward via the
/// same interface; see ENHANCEMENT_GUIDE.md.
/// </summary>
public sealed class ProofStateEncoder
{
    public const int Dimension = 64;

    private readonly Dictionary<string, int> _tacticVocab;
    private readonly ILogger<ProofStateEncoder> _log;

    public ProofStateEncoder(
        IOptions<NexusConfig> config,
        ILogger<ProofStateEncoder> log)
    {
        _log = log;
        _tacticVocab = LoadTacticVocab(config.Value.TacticVocabPath);
    }

    public float[] Encode(ProofState state)
    {
        var v = new float[Dimension];

        EncodeTacticBag(state, v.AsSpan(0, 8));
        EncodeGoalShape(state, v.AsSpan(8, 24));
        EncodeHypothesisFingerprint(state, v.AsSpan(32, 16));
        EncodeRecentTactics(state, v.AsSpan(48, 8));
        EncodeScalars(state, v.AsSpan(56, 4));
        EncodeDomain(state, v.AsSpan(60, 4));

        L2Normalize(v);
        return v;
    }

    /// <summary>
    /// Builds the deterministic canonical representation used by graph-first planning.
    /// This structure is intentionally symbolic and stable across runs.
    /// </summary>
    public CanonicalGoalState BuildCanonicalGoalState(ProofState state)
    {
        return new CanonicalGoalState
        {
            DomainTag = state.DomainTag.Trim().ToLowerInvariant(),
            GoalShapes = state.PendingGoals
                .Select(NormalizeGoalShape)
                .Where(s => s.Length > 0)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray(),
            HypothesisShapes = state.Hypotheses
                .Select(NormalizeGoalShape)
                .Where(s => s.Length > 0)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray(),
            TacticStemHistory = state.TacticHistory
                .TakeLast(6)
                .Select(NormalizeTactic)
                .Where(t => t.Length > 0)
                .ToArray(),
            SorryCount = state.SorryCount,
            ErrorCount = state.ErrorMessages.Length,
            GoalDepth = AverageGoalDepth(state.PendingGoals),
        };
    }

    /// <summary>
    /// Stable hash of the canonical representation. Used as planner state key.
    /// </summary>
    public string ComputeCanonicalStateHash(ProofState state) =>
        ComputeCanonicalStateHash(BuildCanonicalGoalState(state));

    /// <summary>
    /// Stable hash of a canonical goal state. Same canonical content => same hash.
    /// </summary>
    public static string ComputeCanonicalStateHash(CanonicalGoalState state)
    {
        var sb = new StringBuilder();
        sb.Append(state.DomainTag).Append('|');
        sb.Append(state.SorryCount).Append('|');
        sb.Append(state.ErrorCount).Append('|');
        sb.Append(state.GoalDepth).Append('|');
        sb.AppendJoin(";", state.GoalShapes).Append('|');
        sb.AppendJoin(";", state.HypothesisShapes).Append('|');
        sb.AppendJoin(";", state.TacticStemHistory);
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Cosine similarity between two L2-normalized vectors == dot product.</summary>
    public static float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("Vector dimensions must match");
        float dot = 0;
        for (int i = 0; i < a.Length; i++) dot += a[i] * b[i];
        return dot;
    }

    // ---- Slot encoders ----

    private void EncodeTacticBag(ProofState state, Span<float> slot)
    {
        var counts = new int[8];
        var total = 0;
        foreach (var tactic in state.TacticHistory)
        {
            if (_tacticVocab.TryGetValue(NormalizeTactic(tactic), out var idx))
            {
                counts[idx % 8]++;
                total++;
            }
        }
        if (total == 0) return;
        for (int i = 0; i < 8; i++) slot[i] = (float)counts[i] / total;
    }

    private static void EncodeGoalShape(ProofState state, Span<float> slot)
    {
        var buckets = slot.Length; // 24 buckets — parameterised so callers can resize
        foreach (var goal in state.PendingGoals)
        {
            var tokens = goal.Split([' ', '\t', '\n', '(', ')', ',', '.', ':'],
                StringSplitOptions.RemoveEmptyEntries);
            for (int i = 1; i < tokens.Length; i++)
            {
                var bigram = $"{tokens[i - 1]}::{tokens[i]}";
                var bucket = StableHash(bigram) % buckets;
                slot[bucket] += 1f;
            }
        }
        L2NormalizeSlot(slot);
    }

    private static void EncodeHypothesisFingerprint(ProofState state, Span<float> slot)
    {
        var hashes = state.Hypotheses
            .Select(h => StableHash(h.Trim()))
            .OrderBy(h => h)
            .Take(16)
            .ToArray();
        for (int i = 0; i < hashes.Length; i++)
            slot[i] = (float)(hashes[i] % 1000) / 1000f;
    }

    private static void EncodeRecentTactics(ProofState state, Span<float> slot)
    {
        var recent = state.TacticHistory.TakeLast(4).ToArray();
        for (int i = 1; i < recent.Length; i++)
        {
            var bigram = $"{recent[i - 1]}::{recent[i]}";
            var bucket = StableHash(bigram) % 8;
            slot[bucket] += 1f / Math.Max(1, recent.Length - 1);
        }
    }

    private static void EncodeScalars(ProofState state, Span<float> slot)
    {
        slot[0] = MathF.Min(1f, state.SorryCount / 10f);
        slot[1] = MathF.Min(1f, state.ErrorMessages.Length / 10f);
        slot[2] = MathF.Min(1f, AverageGoalDepth(state.PendingGoals) / 20f);
        slot[3] = MathF.Min(1f, state.Hypotheses.Length / 20f);
    }

    private static void EncodeDomain(ProofState state, Span<float> slot)
    {
        var domain = state.DomainTag.ToLowerInvariant();
        var idx = domain switch
        {
            "combinatorics" => 0,
            "algebra"       => 1,
            "analysis"      => 2,
            _               => 3,
        };
        slot[idx] = 1f;
    }

    // ---- Helpers ----

    private static int AverageGoalDepth(string[] goals)
    {
        if (goals.Length == 0) return 0;
        var total = 0;
        foreach (var g in goals)
        {
            var depth = 0;
            foreach (var c in g)
                if (c == '(' || c == '⟨' || c == '⦃') depth++;
            total += depth;
        }
        return total / goals.Length;
    }

    private static int StableHash(string s)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        return Math.Abs(BitConverter.ToInt32(bytes, 0));
    }

    private static readonly Regex MultiSpace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex MetaVar = new(@"\?m_\d+", RegexOptions.Compiled);
    private static readonly Regex NatNumber = new(@"\b\d+\b", RegexOptions.Compiled);

    private static string NormalizeGoalShape(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var s = text.Trim().ToLowerInvariant();
        s = MetaVar.Replace(s, "?m");
        s = NatNumber.Replace(s, "#");
        s = MultiSpace.Replace(s, " ");
        return s;
    }

    private static string NormalizeTactic(string tactic)
    {
        // Strip arguments — "rw [foo]" → "rw"
        var firstSpace = tactic.IndexOfAny([' ', '\t', '[', '(']);
        return (firstSpace > 0 ? tactic[..firstSpace] : tactic).Trim();
    }

    private static void L2Normalize(float[] v)
    {
        float sumSq = 0;
        foreach (var x in v) sumSq += x * x;
        if (sumSq < 1e-9f) return;
        var inv = 1f / MathF.Sqrt(sumSq);
        for (int i = 0; i < v.Length; i++) v[i] *= inv;
    }

    private static void L2NormalizeSlot(Span<float> slot)
    {
        float sumSq = 0;
        foreach (var x in slot) sumSq += x * x;
        if (sumSq < 1e-9f) return;
        var inv = 1f / MathF.Sqrt(sumSq);
        for (int i = 0; i < slot.Length; i++) slot[i] *= inv;
    }

    private Dictionary<string, int> LoadTacticVocab(string path)
    {
        if (!File.Exists(path))
        {
            _log.LogWarning("Tactic vocab not found at {Path}; using default 32-entry list", path);
            return DefaultTacticVocab();
        }

        var json = File.ReadAllText(path);
        var entries = JsonSerializer.Deserialize<string[]>(json) ?? [];
        var dict = new Dictionary<string, int>(entries.Length);
        for (int i = 0; i < entries.Length; i++) dict[entries[i]] = i;
        return dict;
    }

    private static Dictionary<string, int> DefaultTacticVocab()
    {
        var tactics = new[]
        {
            "exact", "apply", "intro", "intros", "rfl", "rw", "simp", "ring",
            "linarith", "omega", "norm_num", "constructor", "use", "cases",
            "rcases", "obtain", "have", "show", "refine", "by_contra", "push_neg",
            "exact?", "decide", "tauto", "field_simp", "induction", "subst",
            "split_ifs", "calc", "trans", "symm", "contradiction",
        };
        var dict = new Dictionary<string, int>(tactics.Length);
        for (int i = 0; i < tactics.Length; i++) dict[tactics[i]] = i;
        return dict;
    }
}

/// <summary>
/// Canonical symbolic state key used by graph-first planning.
/// Goal/hypothesis arrays are normalized and sorted to reduce presentation noise.
/// </summary>
public sealed record CanonicalGoalState
{
    public required string DomainTag { get; init; }
    public required string[] GoalShapes { get; init; }
    public required string[] HypothesisShapes { get; init; }
    public required string[] TacticStemHistory { get; init; }
    public required int SorryCount { get; init; }
    public required int ErrorCount { get; init; }
    public required int GoalDepth { get; init; }
}

/// <summary>
/// Strict deterministic action schema for planner expansion.
/// The planner only applies actions that can be represented by this contract.
/// </summary>
public sealed record CanonicalActionCandidate
{
    public required string ActionId { get; init; }
    public required string TacticText { get; init; }
    public required string Source { get; init; }
    public required float RankScore { get; init; }
    public required float Similarity { get; init; }
    public required float HistoricalSuccessRate { get; init; }
}
