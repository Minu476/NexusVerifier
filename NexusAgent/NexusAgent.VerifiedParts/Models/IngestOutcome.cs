namespace NexusAgent.VerifiedParts.Models;

/// <summary>Discriminated union returned by <see cref="VerifiedPartIngestor.IngestAsync"/>.</summary>
public abstract record IngestOutcome
{
    /// <summary>Part passed all gates and was written to at least one sink.</summary>
    /// <param name="FossilId">The fossil vault ID, or <c>null</c> if the fossil sink was not active.</param>
    /// <param name="SinksWritten">Names of the sinks that accepted the write (e.g. "fossil", "landmark").</param>
    public sealed record Ingested(string PartName, string? FossilId, string[] AxiomProfile, string[] SinksWritten)
        : IngestOutcome;

    /// <summary>Part was rejected at one of the gates; nothing was written.</summary>
    public sealed record Rejected(string PartName, string Reason)
        : IngestOutcome;

    public bool IsIngested => this is Ingested;
}

