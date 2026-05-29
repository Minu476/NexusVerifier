namespace NexusAgent.Core.Models;

public sealed class GraphProposalTelemetry
{
    public int Tier075Attempts { get; set; } = 0;
    public int Tier075CompileSuccesses { get; set; } = 0;
    public int Tier075SorryReductions { get; set; } = 0;
    public int Tier075AttributedWins { get; set; } = 0;

    public void MergeFrom(GraphProposalTelemetry other)
    {
        Tier075Attempts += other.Tier075Attempts;
        Tier075CompileSuccesses += other.Tier075CompileSuccesses;
        Tier075SorryReductions += other.Tier075SorryReductions;
        Tier075AttributedWins += other.Tier075AttributedWins;
    }

    public void LogMetrics(string problemId)
    {
        Console.WriteLine($"--- Graph Proposal Telemetry [{problemId}] ---");
        Console.WriteLine($"Proposals Attempted (Tier 0.75): {Tier075Attempts}");
        Console.WriteLine($"Compile Passes:                  {Tier075CompileSuccesses} ({(Tier075Attempts > 0 ? (Tier075CompileSuccesses * 100.0 / Tier075Attempts) : 0):F1}%)");
        Console.WriteLine($"Strict 'sorry' Reductions:       {Tier075SorryReductions}");
        Console.WriteLine($"Wins Directly Attributed:        {Tier075AttributedWins}");
        Console.WriteLine("--------------------------------------------");
    }
}