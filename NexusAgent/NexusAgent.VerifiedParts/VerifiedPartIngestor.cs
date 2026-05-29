using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using NexusAgent.Core.Models;
using NexusAgent.Core.Oracle;
using NexusAgent.VerifiedParts.Models;
using NexusAgent.VerifiedParts.Sinks;

namespace NexusAgent.VerifiedParts;

/// <summary>
/// Gate-checks a <see cref="VerifiedPart"/> (compile → axiom profile → scope guard)
/// and, if it passes, fans out to each enabled <see cref="IPartSink"/>.
///
/// Gate sequencing
/// ───────────────
///   (a) <c>ILeanOracle.CompileAsync</c>   → 0 errors, 0 sorries
///   (b) <c>AxiomChecker.CheckAsync</c>    → no sorryAx; native check per policy
///   (c) Scope guard                        → Full requires FullScopeConfirmed=true
///
/// The gate runs once regardless of how many sinks are active.
/// Each enabled sink is invoked sequentially; a sink failure is logged as a warning
/// but does not prevent other sinks from running or flip the outcome to Rejected.
///
/// Sink selection
/// ──────────────
/// Call <c>IngestAsync(part, activeSinkNames: new[]{"fossil","landmark"}, ct)</c>.
/// Passing <c>null</c> (or omitting) uses all registered sinks.
///
/// native_decide policy (env NEXUS_PARTS_NATIVE_DECIDE)
/// ──────────────────────────────────────────────────────
///   "reject" (default) — any native/ofReduceBool axiom → Rejected
///   "flag"             — ingested but with :native-flagged suffix in provenance tag
/// </summary>
public sealed class VerifiedPartIngestor
{
    // Unique per process-run so ingest batches can be distinguished in log/vault.
    public static readonly string IngestRunId = Guid.NewGuid().ToString("N")[..8];

    private readonly ILeanOracle                   _lean;
    private readonly AxiomChecker                  _axiomChecker;
    private readonly IReadOnlyList<IPartSink>      _allSinks;
    private readonly ILogger<VerifiedPartIngestor> _log;
    private readonly NativeDecidePolicy            _nativePolicy;

    public enum NativeDecidePolicy { Reject, Flag }

    public VerifiedPartIngestor(
        ILeanOracle                   lean,
        AxiomChecker                  axiomChecker,
        IEnumerable<IPartSink>        sinks,
        ILogger<VerifiedPartIngestor> log)
    {
        _lean         = lean;
        _axiomChecker = axiomChecker;
        _allSinks     = sinks.ToList();
        _log          = log;

        var env = Environment.GetEnvironmentVariable("NEXUS_PARTS_NATIVE_DECIDE") ?? "reject";
        _nativePolicy = env.Equals("flag", StringComparison.OrdinalIgnoreCase)
            ? NativeDecidePolicy.Flag
            : NativeDecidePolicy.Reject;
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <param name="activeSinkNames">
    /// Names of sinks to write to (e.g. <c>new[]{"fossil","landmark"}</c>).
    /// Pass <c>null</c> to use all registered sinks.
    /// </param>
    public async Task<IngestOutcome> IngestAsync(
        VerifiedPart                 part,
        IReadOnlyCollection<string>? activeSinkNames,
        CancellationToken            ct)
    {
        // ── (a) Compile gate ──────────────────────────────────────────────────
        var sketch   = BuildProofSketch(part);
        var compiled = await _lean.CompileAsync(sketch, ct);

        if (!compiled.Compiled || compiled.Errors.Length > 0)
            return Reject(part, $"compile error: {compiled.Errors.FirstOrDefault() ?? "compile failed"}");
        if (compiled.SorryCount != 0)
            return Reject(part, $"non-zero sorry count: {compiled.SorryCount}");

        // ── (b) Axiom gate ────────────────────────────────────────────────────
        var axioms = await _axiomChecker.CheckAsync(
            part.ImportsHeader, part.StatementText, part.ProofBlock, ct);

        if (axioms is null)
            return Reject(part, "axiom check failed — lean did not emit #print axioms output (compile may have errored)");
        if (AxiomChecker.ContainsSorryAx(axioms))
            return Reject(part, $"axiom closure contains sorryAx: [{string.Join(", ", axioms)}]");

        var nativeEscape = AxiomChecker.ContainsNativeEscape(axioms);
        if (nativeEscape && _nativePolicy == NativeDecidePolicy.Reject)
            return Reject(part, $"native/unsafe axioms present (policy=reject): [{string.Join(", ", axioms)}]");

        // ── (c) Scope guard ───────────────────────────────────────────────────
        if (part.Scope == PartScope.Full && !part.FullScopeConfirmed)
            return Reject(part, "Scope=Full requires FullScopeConfirmed=true (set by a human after statement-fidelity review)");

        // ── (d) Fan out to enabled sinks ──────────────────────────────────────
        var openGoal    = BuildOpenGoalState(part);
        var activeSinks = SelectSinks(activeSinkNames);

        string?      fossilId     = null;
        var          sinksWritten = new List<string>();

        foreach (var sink in activeSinks)
        {
            try
            {
                var artifactId = await sink.WriteAsync(part, axioms, openGoal, ct);
                sinksWritten.Add(sink.Name);
                if (sink.Name == "fossil") fossilId = artifactId;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Sink '{Sink}' failed for {Part} — continuing with remaining sinks",
                    sink.Name, part.PartName);
            }
        }

        _log.LogInformation(
            "Ingested {Part} → sinks=[{Sinks}] scope={Scope} held={H} axioms=[{Ax}]",
            part.PartName, string.Join(",", sinksWritten), part.Scope, part.IsHeldOut,
            string.Join(", ", axioms));

        return new IngestOutcome.Ingested(part.PartName, fossilId, axioms, [.. sinksWritten]);
    }

    // ── dry-run (gate only, no write) ────────────────────────────────────────

    public async Task<GateResult> DryRunAsync(VerifiedPart part, CancellationToken ct)
    {
        var sketch   = BuildProofSketch(part);
        var compiled = await _lean.CompileAsync(sketch, ct);

        if (!compiled.Compiled || compiled.Errors.Length > 0)
            return GateResult.Fail($"compile error: {compiled.Errors.FirstOrDefault() ?? "unknown"}");
        if (compiled.SorryCount != 0)
            return GateResult.Fail($"non-zero sorry count: {compiled.SorryCount}");

        var axioms = await _axiomChecker.CheckAsync(
            part.ImportsHeader, part.StatementText, part.ProofBlock, ct);
        if (axioms is null)
            return GateResult.Fail("axiom check failed — no #print axioms output");
        if (AxiomChecker.ContainsSorryAx(axioms))
            return GateResult.Fail($"sorryAx in axiom closure: [{string.Join(", ", axioms)}]");
        if (AxiomChecker.ContainsNativeEscape(axioms) && _nativePolicy == NativeDecidePolicy.Reject)
            return GateResult.Fail($"native axioms (policy=reject): [{string.Join(", ", axioms)}]");
        if (part.Scope == PartScope.Full && !part.FullScopeConfirmed)
            return GateResult.Fail("Scope=Full requires FullScopeConfirmed=true");

        return GateResult.Ok(axioms);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private IEnumerable<IPartSink> SelectSinks(IReadOnlyCollection<string>? names) =>
        names is null
            ? _allSinks
            : _allSinks.Where(s => names.Contains(s.Name, StringComparer.OrdinalIgnoreCase));

    private static string BuildProofSketch(VerifiedPart part) =>
        $"""
        {part.ImportsHeader}

        -- NexusAgent.VerifiedParts kernel gate: {part.PartName}
        private noncomputable def _nexus_part_check : {part.StatementText} := {part.ProofBlock}
        """;

    /// <summary>
    /// Builds the "open goal" ProofState passed to sinks for vector encoding.
    /// PendingGoals = [StatementText] so that similarity retrieval fires when
    /// a new problem's open goal resembles this statement.
    /// </summary>
    private static ProofState BuildOpenGoalState(VerifiedPart part)
    {
        var sketchHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(part.StatementText))).ToLowerInvariant();

        return ProofState.Empty(part.DomainTag) with
        {
            PendingGoals = [part.StatementText],
            SorryCount   = 1,
            SketchHash   = sketchHash,
        };
    }

    private IngestOutcome Reject(VerifiedPart part, string reason)
    {
        _log.LogWarning("Rejected verified part {Part}: {Reason}", part.PartName, reason);
        return new IngestOutcome.Rejected(part.PartName, reason);
    }
}
