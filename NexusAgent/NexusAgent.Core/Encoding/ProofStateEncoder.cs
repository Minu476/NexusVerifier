using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexusAgent.Core.Configuration;
using NexusAgent.Core.Models;

namespace NexusAgent.Core.Encoding;

/// <summary>
/// Encodes a Lean ProofState into a 64-dim L2-normalized float vector.
///
/// Layout (see SPEC.md §4.1):
///   [ 0..15]  Tactic bag (TF over 200-entry Mathlib tactic vocab, top-16 buckets)
///   [16..31]  Goal-shape fingerprint (hashed token bigrams of pending goals)
///   [32..47]  Hypothesis fingerprint (sorted hashes, top-16)
///   [48..55]  Last-4 tactic n-grams (hashed 2-grams)
///   [56..59]  Scalars: sorry_count, error_count, goal_depth, hypothesis_count
///   [60..63]  Domain one-hot (combinatorics/algebra/analysis/other)
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

        EncodeTacticBag(state, v.AsSpan(0, 16));
        EncodeGoalShape(state, v.AsSpan(16, 16));
        EncodeHypothesisFingerprint(state, v.AsSpan(32, 16));
        EncodeRecentTactics(state, v.AsSpan(48, 8));
        EncodeScalars(state, v.AsSpan(56, 4));
        EncodeDomain(state, v.AsSpan(60, 4));

        L2Normalize(v);
        return v;
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
        var counts = new int[16];
        var total = 0;
        foreach (var tactic in state.TacticHistory)
        {
            if (_tacticVocab.TryGetValue(NormalizeTactic(tactic), out var idx))
            {
                counts[idx % 16]++;
                total++;
            }
        }
        if (total == 0) return;
        for (int i = 0; i < 16; i++) slot[i] = (float)counts[i] / total;
    }

    private static void EncodeGoalShape(ProofState state, Span<float> slot)
    {
        foreach (var goal in state.PendingGoals)
        {
            var tokens = goal.Split([' ', '\t', '\n', '(', ')', ',', '.', ':'],
                StringSplitOptions.RemoveEmptyEntries);
            for (int i = 1; i < tokens.Length; i++)
            {
                var bigram = $"{tokens[i - 1]}::{tokens[i]}";
                var bucket = StableHash(bigram) % 16;
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
