using System.Text.Json.Serialization;
using NexusAgent.VerifiedParts.Models;

namespace NexusAgent.VerifiedParts.Models;

/// <summary>
/// A Lean theorem part to be gate-checked and ingested into the fossil vault.
///
/// JSON-serializable so batch runs can pass parts via --from-json.
/// Scope inference: use <see cref="VerifiedPart.InferScope"/> for a conservative default;
/// never set <see cref="Scope"/> = Full without setting <see cref="FullScopeConfirmed"/> = true.
/// </summary>
public sealed record VerifiedPart
{
    /// <summary>Fully qualified Lean declaration name, e.g. "Erdos1.erdos_1.variants.weaker".</summary>
    public required string PartName { get; init; }

    /// <summary>Opaque problem identifier (used for fossil tagging / holdout queries).</summary>
    public required string ProblemId { get; init; }

    /// <summary>Domain tag fed to ProofStateEncoder, e.g. "combinatorics".</summary>
    public required string DomainTag { get; init; }

    /// <summary>The goal statement text (for the encoder's PendingGoals, not the proof).</summary>
    public required string StatementText { get; init; }

    /// <summary>The verified tactic / term block (the proof body after `:=`).</summary>
    public required string ProofBlock { get; init; }

    /// <summary>Import lines to include in the standalone compile sketch.</summary>
    public required string ImportsHeader { get; init; }

    /// <summary>Scope classification. Default Unknown — conservatively safe.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PartScope Scope { get; init; } = PartScope.Unknown;

    /// <summary>
    /// Set true only when a human has confirmed the statement is equivalent to the
    /// full conjecture. Required before Scope = Full is accepted by the gate.
    /// </summary>
    public bool FullScopeConfirmed { get; init; }

    /// <summary>
    /// When true, this part may not appear as a scan/benchmark target in the same set
    /// it was seeded from. Default true — safe to flip off only for pure seed sets
    /// with no overlap with any benchmark.
    /// </summary>
    public bool IsHeldOut { get; init; } = true;

    /// <summary>Human-readable source label, e.g. "fc-upstream", "llm-3x", "manual".</summary>
    public string Source { get; init; } = "unknown";

    /// <summary>
    /// Conservative scope inference from the part name.
    /// Returns Weaker / Instance / Part / Unknown — never Full.
    /// </summary>
    public static PartScope InferScope(string partName)
    {
        if (partName.Contains(".variants."))  return PartScope.Weaker;
        if (partName.Contains(".parts."))     return PartScope.Part;
        if (System.Text.RegularExpressions.Regex.IsMatch(
                partName, @"_\d+$|least_N_\d+|_singleton$|_d\d+$|_instance$"))
            return PartScope.Instance;
        return PartScope.Unknown;
    }
}
