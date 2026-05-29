namespace NexusAgent.Core.Memory;

/// <summary>
/// One AND-OR backward-chaining hyperedge from the ErdosHypergraph Lean engine.
/// Semantics: to prove <see cref="Output"/>, first prove every string in <see cref="Inputs"/>.
/// Stored in Neo4j as <c>:HyperedgeRecord</c> for persistent reuse across runs.
/// </summary>
public sealed record HyperedgeRecord
{
    /// <summary>
    /// Stable identifier: SHA256 hex prefix of (LemmaName + ":" + Output).
    /// Deterministic across runs so MERGE is idempotent.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>Lean 4 fully-qualified declaration name (e.g. <c>Nat.add_comm</c>).</summary>
    public required string LemmaName { get; init; }

    /// <summary>Pretty-printed conclusion type — the goal shape this edge closes.</summary>
    public required string Output { get; init; }

    /// <summary>
    /// UInt64 hash of <see cref="Output"/>, matching the key used in the Lean
    /// <c>HashMap UInt64 (Array HgEdge)</c> so the C# agent can reconstruct the
    /// same bucket layout if needed.
    /// </summary>
    public required ulong OutputHash { get; init; }

    /// <summary>
    /// Pretty-printed types of Prop-kinded ∀ binders — the sub-goals required
    /// before this edge can be applied. Empty for leaf edges (proven data facts).
    /// </summary>
    public required string[] Inputs { get; init; }

    /// <summary>UTC timestamp when <c>buildHypergraph</c> produced this edge.</summary>
    public required DateTime BuiltAt { get; init; }

    /// <summary>Short identifier for the Lean run that produced this edge.</summary>
    public required string SeedRun { get; init; }
}
