using NexusAgent.Core.Memory;

namespace NexusAgent.Core.Planning;

/// <summary>
/// A node in an AND-join derivation tree built by <see cref="HyperedgeComposer"/>.
///
/// <para>Semantics:</para>
/// <list type="bullet">
///   <item>The <see cref="Edge"/> closes one goal (its <c>Output</c>).</item>
///   <item>Each entry in <see cref="PremiseDerivations"/> closes the corresponding
///       premise in <see cref="HyperedgeRecord.Inputs"/> (same order).</item>
///   <item>Leaf nodes have <c>Edge.Inputs.Length == 0</c> and an empty
///       <see cref="PremiseDerivations"/> list — no sub-goals required.</item>
/// </list>
///
/// <para>The linear fossil/tactic chain is the degenerate case where every node
/// is a leaf or has exactly one premise.</para>
/// </summary>
public sealed record DerivationNode
{
    /// <summary>The stored hyperedge that closes this node's goal.</summary>
    public required HyperedgeRecord Edge { get; init; }

    /// <summary>
    /// One sub-derivation per premise in <see cref="HyperedgeRecord.Inputs"/>,
    /// in the same order. Empty for leaf nodes.
    /// </summary>
    public required IReadOnlyList<DerivationNode> PremiseDerivations { get; init; }

    /// <summary>True when the edge needs no premises — directly provable.</summary>
    public bool IsLeaf => Edge.Inputs.Length == 0;

    /// <summary>
    /// Maximum depth of the derivation tree.
    /// 0 for a leaf, 1 for a single AND-join, etc.
    /// </summary>
    public int Depth => IsLeaf ? 0 : 1 + PremiseDerivations.Max(d => d.Depth);
}
