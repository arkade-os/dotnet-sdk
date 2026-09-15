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

    /// <summary>Machine-readable diagnostic; unknown codes remain available without changing Reason.</summary>
    public string? ErrorCode { get; init; }
    /// <summary>The field identified by the solver's diagnostic.</summary>
    public string? Field { get; init; }
    /// <summary>The rejected value.</summary>
    public long? Actual { get; init; }
    /// <summary>The expected value, when provided.</summary>
    public long? Expected { get; init; }
    /// <summary>The applicable limit, when provided.</summary>
    public long? Limit { get; init; }
    /// <summary>Diagnostic units, such as blocks or sats.</summary>
    public string? Unit { get; init; }
}
