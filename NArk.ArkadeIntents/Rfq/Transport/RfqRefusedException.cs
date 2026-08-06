namespace NArk.ArkadeIntents.Rfq;

/// <summary>Thrown when a solver declines a request.</summary>
public sealed class RfqRefusedException : Exception
{
    /// <summary>Creates the exception from a refusal payload.</summary>
    /// <param name="reason">The reason from the closed set; unrecognised wire values arrive as <see cref="RfqRefusalReason.Unknown"/>.</param>
    /// <param name="rfqId">The correlation id refused, when the solver echoed one.</param>
    /// <param name="detail">The solver's optional human-readable elaboration. Never branch on it.</param>
    public RfqRefusedException(RfqRefusalReason reason, string? rfqId = null, string? detail = null)
        : base($"solver refused: {reason}{(detail is null ? "" : $" ({detail})")}")
    {
        Reason = reason;
        RfqId = rfqId;
        Detail = detail;
    }

    /// <summary>Why the solver declined.</summary>
    public RfqRefusalReason Reason { get; }

    /// <summary>The correlation id refused, if the solver echoed one.</summary>
    public string? RfqId { get; }

    /// <summary>The solver's optional elaboration, for humans and logs only.</summary>
    public string? Detail { get; }
}
