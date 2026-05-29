namespace NexusAgent.VerifiedParts.Models;

/// <summary>
/// Declares how close a verified part is to the full conjecture.
/// Never auto-promote to Full — that requires a human confirmation flag.
/// </summary>
public enum PartScope
{
    /// <summary>Scope not yet determined. Zero-value, so default(PartScope) is the safe default that conservatively blocks Full credit.</summary>
    Unknown  = 0,
    /// <summary>A weaker version (e.g. .variants.weaker, .variants.consequence).</summary>
    Weaker   = 1,
    /// <summary>A specific numeric instance of a universally-quantified statement.</summary>
    Instance = 2,
    /// <summary>One named part of a multi-part problem (e.g. .parts.i, .parts.ii).</summary>
    Part     = 3,
    /// <summary>The exact conjecture statement. Requires FullScopeConfirmed = true.</summary>
    Full     = 4,
}
