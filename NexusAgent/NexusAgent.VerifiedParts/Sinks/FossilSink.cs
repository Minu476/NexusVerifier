using Microsoft.Extensions.Logging;
using NexusAgent.Core.Encoding;
using NexusAgent.Core.Memory;
using NexusAgent.Core.Models;
using NexusAgent.VerifiedParts.Models;

namespace NexusAgent.VerifiedParts.Sinks;

/// <summary>
/// Sink that writes a verified part as a <see cref="ProofFossil"/> in the vault.
///
/// This is "Idea 1" — the fossil vault is retrieved by <c>NearestFossilsAsync</c>
/// to fill <c>sorry</c> positions in the LLM prover loop.
///
/// Provenance is encoded in <c>SourceProblems</c> as a structured tag:
///   <c>verified-part:{PartName}:scope={Scope}:held={IsHeldOut}[:native-flagged]</c>
/// so downstream readers can filter on scope or held-out status without a schema change.
/// </summary>
public sealed class FossilSink : IPartSink
{
    public string Name => "fossil";

    private readonly INeo4jClient           _neo4j;
    private readonly ProofStateEncoder      _encoder;
    private readonly ILogger<FossilSink>    _log;

    public FossilSink(
        INeo4jClient        neo4j,
        ProofStateEncoder   encoder,
        ILogger<FossilSink> log)
    {
        _neo4j   = neo4j;
        _encoder = encoder;
        _log     = log;
    }

    public async Task<string> WriteAsync(
        VerifiedPart part, string[] axioms, ProofState openGoal, CancellationToken ct)
    {
        var fossilId = Guid.NewGuid().ToString("N");

        // Fold scope + held-out flag into the source tag so they are recoverable
        // downstream without touching the ProofFossil schema or Core.
        var nativeSuffix = AxiomChecker.ContainsNativeEscape(axioms) ? ":native-flagged" : "";
        var sourceTag    = $"verified-part:{part.PartName}:scope={part.Scope}:held={part.IsHeldOut}{nativeSuffix}";

        var fossil = new ProofFossil
        {
            Id                  = fossilId,
            SubgoalText         = part.StatementText,   // goal text → vector similarity
            TacticBlock         = part.ProofBlock,
            StateVector         = _encoder.Encode(openGoal),
            DomainTag           = part.DomainTag,
            SorryCountBefore    = 1,                    // "one open goal"
            SorryCountAfter     = 0,                    // fully proved
            ProvedAt            = DateTime.UtcNow,
            SourceProblems      = [sourceTag],
            UseCount            = 0,
            CompilationVerified = true,                 // kernel-checked by gate before this call
            RunId               = VerifiedPartIngestor.IngestRunId,
        };

        await _neo4j.UpsertFossilAsync(fossil, ct);
        _log.LogDebug("FossilSink: {FId} ← {Part}", fossilId[..8], part.PartName);
        return fossilId;
    }
}
