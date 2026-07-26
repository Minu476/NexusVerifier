namespace NexusAgent.ToposExperiment.Search;

/// <summary>The three experimental arms (D1). Verbatim from RlbExperiment — same semantics.</summary>
public enum SearchArm
{
    /// <summary>Baseline-N: order by HyperEdge.Id.StableIdentity lexicographically. Zero learning.</summary>
    BaselineN,

    /// <summary>Baseline-H: order by SuccessRate desc, TransitionCount desc, then Id. Historical stats only.</summary>
    BaselineH,

    /// <summary>Learned: order by HyperEdge.Evaluate(ctx) desc. Uses the parametric edge substrate.</summary>
    Learned,
}
