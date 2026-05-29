namespace NexusAgent.VerifiedParts.Models;

/// <summary>Result of the kernel gate check (compile + axiom profile).</summary>
public sealed record GateResult(bool Passed, string Reason, string[] AxiomProfile)
{
    public static GateResult Ok(string[] axioms) => new(true, string.Empty, axioms);
    public static GateResult Fail(string reason) => new(false, reason, []);
}
