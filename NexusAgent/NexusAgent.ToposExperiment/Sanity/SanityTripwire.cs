namespace NexusAgent.ToposExperiment.Sanity;

/// <summary>
/// Section 3.5 sanity tripwire. Verbatim logic from
/// <c>NexusAgent.RlbExperiment.Sanity.SanityTripwire</c>; the obsolete
/// <c>AssertThetaUpdated(InMemoryGraphMemory, int)</c> overload is dropped because it was an empty
/// stub in the V2 version anyway and the concrete-type coupling is what we're explicitly avoiding
/// here (the whole point of this project is the backend is swappable).
/// </summary>
public static class SanityTripwire
{
    /// <param name="thetaEdgeCount">Number of HyperEdges whose theta was reinforced during the first training pass.</param>
    public static void Assert(int thetaEdgeCount, int episodesRun)
    {
        if (thetaEdgeCount == 0)
            throw new InvalidOperationException(
                $"SanityTripwire FAILED: ThetaEdgeCount == 0 after {episodesRun} training episodes. " +
                "No HyperEdge theta was reinforced. Possible causes: " +
                "(a) no training episode succeeded or failed with a non-null trajectory node, " +
                "(b) BackwardReinforce produced zero Q-values, " +
                "(c) FiredHyperedgeId was never set on trajectory nodes.");
    }
}
