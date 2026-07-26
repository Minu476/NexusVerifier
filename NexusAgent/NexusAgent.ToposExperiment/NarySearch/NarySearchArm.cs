namespace NexusAgent.ToposExperiment.NarySearch;

/// <summary>
/// The three experimental arms for the n-ary chainer — same semantics as
/// <see cref="Search.SearchArm"/>, but the candidate ordering now reads directly off the Topos
/// edge-vertex's properties (resolved through the kernel's typed property pools), not off a
/// projected V2 <c>HyperEdge</c>.
/// </summary>
public enum NarySearchArm
{
    /// <summary>Order by the Topos edge-vertex's Handle.Index (ingest order — zero learning).</summary>
    BaselineN,

    /// <summary>Order by the edge-vertex's EdgeStatistics (SuccessRate/TransitionCount/Confidence).</summary>
    BaselineH,

    /// <summary>Order by the edge-vertex's LearnableEdge.Evaluate(features) — the parametric arm.</summary>
    Learned,
}
