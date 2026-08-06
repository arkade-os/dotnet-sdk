namespace NArk.ArkadeIntents.Rfq;

/// <summary>A refusal carrying a reason from the closed set. Corridor-agnostic: it has no profile.</summary>
public sealed class RfqRefusal
{
    /// <summary>Envelope version.</summary>
    public int V { get; init; }

    /// <summary>Envelope discriminator.</summary>
    public string? Type { get; init; }

    /// <summary>The correlation id refused, when the solver could parse one.</summary>
    public string? RfqId { get; init; }

    /// <summary>Why the solver declined.</summary>
    public RfqRefusalReason Reason { get; init; }

    /// <summary>An optional human-readable elaboration. Never branch on it.</summary>
    public string? Detail { get; init; }
}
