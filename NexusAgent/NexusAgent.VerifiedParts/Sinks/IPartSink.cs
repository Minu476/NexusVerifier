using NexusAgent.Core.Models;
using NexusAgent.VerifiedParts.Models;

namespace NexusAgent.VerifiedParts.Sinks;

/// <summary>
/// A write destination for a verified part that has passed the gate.
///
/// The gate (compile + axiom + scope) runs exactly once in
/// <see cref="VerifiedPartIngestor.IngestAsync"/>; each enabled sink then
/// receives the same <see cref="VerifiedPart"/>, axiom profile, and open-goal
/// <see cref="ProofState"/> and writes it in whatever format its layer needs.
///
/// Current implementations:
///   "fossil"   — <see cref="FossilSink"/>  → fossil vault (<c>UpsertFossilAsync</c>)
///   "landmark" — <see cref="LandmarkSink"/> → landmark/replay layer (<c>ObserveAsync</c> + <c>RecordTransitionAsync</c>)
///
/// Sinks are selected per CLI invocation via <c>--sinks fossil,landmark</c>.
/// The default (backward-compatible) is <c>fossil</c> only.
/// </summary>
public interface IPartSink
{
    /// <summary>Short, lower-case identifier used in <c>--sinks</c> flag and log output.</summary>
    string Name { get; }

    /// <summary>
    /// Write the verified part. Returns an artifact identifier (fossil ID, landmark ID, …)
    /// that is surfaced in the CLI output and log.
    /// Implementations should throw on failure; the ingestor catches, logs, and skips to
    /// the next sink.
    /// </summary>
    Task<string> WriteAsync(
        VerifiedPart part,
        string[]     axioms,
        ProofState   openGoal,
        CancellationToken ct);
}
