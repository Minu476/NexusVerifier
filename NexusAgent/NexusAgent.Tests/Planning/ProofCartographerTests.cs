using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NexusAgent.Core.Configuration;
using NexusAgent.Core.Encoding;
using NexusAgent.Core.Memory;
using NexusAgent.Core.Models;
using NexusAgent.Core.Planning;

namespace NexusAgent.Tests.Planning;

/// <summary>
/// Phase 5: ProofCartographer — 8 tests including Theory inline data.
/// </summary>
public sealed class ProofCartographerTests
{
    private readonly Mock<INeo4jClient> _neo4j = new();
    private readonly ProofCartographer _cartographer;
    private readonly ProofStateEncoder _encoder;

    public ProofCartographerTests()
    {
        var config = Options.Create(new NexusConfig { TacticVocabPath = "does_not_exist.json" });
        _encoder = new ProofStateEncoder(config, NullLogger<ProofStateEncoder>.Instance);

        _neo4j.Setup(n => n.UpsertLandmarkAsync(It.IsAny<ProofLandmark>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((ProofLandmark lm, CancellationToken _) => lm);

        _neo4j.Setup(n => n.RecordTransitionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<TransitionOutcome>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);

        _neo4j.Setup(n => n.NearbyLandmarksAsync(It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Array.Empty<ProofLandmark>() as IReadOnlyList<ProofLandmark>);

        _cartographer = new ProofCartographer(_neo4j.Object, _encoder,
            NullLogger<ProofCartographer>.Instance);
    }

    [Fact]
    public async Task ObserveAsync_CallsUpsertLandmark()
    {
        var state = ProofState.Empty("algebra");

        await _cartographer.ObserveAsync(state, "p1", TransitionOutcome.Stalled, CancellationToken.None);

        _neo4j.Verify(n => n.UpsertLandmarkAsync(It.IsAny<ProofLandmark>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RecordTransitionAsync_CallsNeo4j()
    {
        var from = ProofState.Empty("algebra") with { SketchHash = "from" };
        var to = ProofState.Empty("algebra") with { SketchHash = "to" };

        await _cartographer.RecordTransitionAsync(from, to, "p1", "exact", TransitionOutcome.Progressed, "ep1", CancellationToken.None);

        _neo4j.Verify(n => n.RecordTransitionAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            TransitionOutcome.Progressed, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetDeadEndHintAsync_NoNearbyLandmarks_ReturnsNull()
    {
        var state = ProofState.Empty("algebra");

        var hint = await _cartographer.GetDeadEndHintAsync(state, CancellationToken.None);

        Assert.Null(hint);
    }

    [Fact]
    public async Task GetDeadEndHintAsync_WithExhaustedLandmark_ReturnsHint()
    {
        // A landmark near-identical to the current state, all dead-ends
        var state = ProofState.Empty("algebra") with { SorryCount = 2 };
        var vec = _encoder.Encode(state);

        var exhaustedLandmark = new ProofLandmark
        {
            Id = "exhausted-1",
            ProblemId = "p1",
            StateVector = vec,   // identical → cosine sim = 1.0
            SorryCount = 2,
            VisitCount = 5,
            DeadEndCount = 5,    // 100% dead-end fraction
            BestOutcome = TransitionOutcome.DeadEnd,
        };

        _neo4j.Setup(n => n.NearbyLandmarksAsync(It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new[] { exhaustedLandmark } as IReadOnlyList<ProofLandmark>);

        var hint = await _cartographer.GetDeadEndHintAsync(state, CancellationToken.None);

        Assert.NotNull(hint);
    }

    [Theory]
    [InlineData(0, 0, 1f)]          // never visited → potential = 1
    [InlineData(1, 0, 1f)]          // 1 visit, 0 dead-ends → (1-0)/√1 = 1
    [InlineData(4, 0, 0.5f)]        // 4 visits, 0 dead-ends → 1/√4 = 0.5
    [InlineData(4, 2, 0.25f)]       // 4 visits, 2 dead-ends → 0.5/√4 = 0.25
    [InlineData(4, 4, 0f)]          // all dead-ends → 0
    public void Potential_Formula_MatchesSpec(int visits, int deadEnds, float expected)
    {
        var landmark = new ProofLandmark
        {
            Id = "test",
            ProblemId = "p",
            StateVector = new float[64],
            SorryCount = 1,
            VisitCount = visits,
            DeadEndCount = deadEnds,
            BestOutcome = TransitionOutcome.Stalled,
        };

        var actual = landmark.Potential;

        Assert.True(Math.Abs(actual - expected) < 1e-5f,
            $"Expected {expected}, got {actual} (visits={visits}, dead={deadEnds})");
    }

    [Fact]
    public async Task ObserveAsync_ReturnsPersisted_Landmark()
    {
        var state = ProofState.Empty("analysis") with { SorryCount = 3 };
        var persisted = new ProofLandmark
        {
            Id = "server-assigned-id",
            ProblemId = "p1",
            StateVector = _encoder.Encode(state),
            SorryCount = 3,
            VisitCount = 2,
            DeadEndCount = 0,
            BestOutcome = TransitionOutcome.Progressed,
        };

        _neo4j.Setup(n => n.UpsertLandmarkAsync(It.IsAny<ProofLandmark>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(persisted);

        var result = await _cartographer.ObserveAsync(state, "p1", TransitionOutcome.Progressed, CancellationToken.None);

        Assert.Equal("server-assigned-id", result.Id);
        Assert.Equal(2, result.VisitCount);
    }
}
