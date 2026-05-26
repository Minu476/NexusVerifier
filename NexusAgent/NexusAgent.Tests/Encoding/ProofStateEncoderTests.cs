using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NexusAgent.Core.Configuration;
using NexusAgent.Core.Encoding;
using NexusAgent.Core.Models;

namespace NexusAgent.Tests.Encoding;

/// <summary>
/// Phase 2: ProofStateEncoder — 5 tests (pure math invariants, no I/O).
/// </summary>
public sealed class ProofStateEncoderTests
{
    private readonly ProofStateEncoder _encoder;

    public ProofStateEncoderTests()
    {
        var config = Options.Create(new NexusConfig { TacticVocabPath = "does_not_exist.json" });
        _encoder = new ProofStateEncoder(config, NullLogger<ProofStateEncoder>.Instance);
    }

    [Fact]
    public void Encode_EmptyState_Returns64DimVector()
    {
        var state = ProofState.Empty("other");
        var vec = _encoder.Encode(state);
        Assert.Equal(64, vec.Length);
    }

    [Fact]
    public void Encode_L2NormalizedOrZero()
    {
        var state = ProofState.Empty("algebra") with
        {
            TacticHistory = ["exact", "apply", "rw"],
            PendingGoals = ["⊢ n + 0 = n"],
        };
        var vec = _encoder.Encode(state);
        var norm = MathF.Sqrt(vec.Sum(x => x * x));
        // Either zero (all-zero state) or unit length
        Assert.True(norm < 1e-6f || Math.Abs(norm - 1f) < 1e-5f,
            $"Norm was {norm:F6}, expected 0 or 1");
    }

    [Fact]
    public void Encode_SameDomain_DifferentFromOtherDomain()
    {
        var s1 = ProofState.Empty("algebra");
        var s2 = ProofState.Empty("combinatorics");
        var v1 = _encoder.Encode(s1);
        var v2 = _encoder.Encode(s2);
        // Domain one-hot differs → vectors differ
        Assert.False(v1.SequenceEqual(v2));
    }

    [Fact]
    public void Encode_DeterministicForSameInput()
    {
        var state = ProofState.Empty("analysis") with
        {
            PendingGoals = ["⊢ x > 0"],
            TacticHistory = ["intro", "linarith"],
            SorryCount = 1,
        };
        var v1 = _encoder.Encode(state);
        var v2 = _encoder.Encode(state);
        Assert.Equal(v1, v2);
    }

    [Fact]
    public void CosineSimilarity_IdenticalVectors_ReturnsOne()
    {
        var state = ProofState.Empty("algebra") with
        {
            TacticHistory = ["simp", "ring"],
        };
        var v = _encoder.Encode(state);
        var sim = ProofStateEncoder.CosineSimilarity(v, v);
        Assert.True(Math.Abs(sim - 1f) < 1e-5f, $"Expected 1, got {sim}");
    }
}
