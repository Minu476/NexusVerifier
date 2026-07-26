namespace NexusAgent.ToposExperiment.Telemetry;

/// <summary>Per-episode metrics recorded during training and eval. Verbatim from RlbExperiment.</summary>
public sealed record EpisodeTelemetry(
    string EpisodeId,
    string TheoremId,
    string GoalHash,
    string Arm,
    bool Solved,
    int Steps,
    int ThetaEdgesReinforced,
    double MeanThetaMagnitude,
    int HyperedgesFired,
    int NegativeReinforced,
    double ElapsedMs);
