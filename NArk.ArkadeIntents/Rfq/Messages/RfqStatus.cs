namespace NArk.ArkadeIntents.Rfq;

/// <summary>
/// The solver's view of a negotiation. Best-effort by design: the settlement is equally observable
/// on-chain, and the spend that settles the contract is the ground truth. Treat this as a
/// convenience, never as the money path.
/// </summary>
/// <typeparam name="TProfile">The corridor's status-profile shape.</typeparam>
public sealed class RfqStatus<TProfile>
{
    /// <summary>Envelope version.</summary>
    public int V { get; init; }

    /// <summary>Envelope discriminator.</summary>
    public string? Type { get; init; }

    /// <summary>The correlation id.</summary>
    public string? RfqId { get; init; }

    /// <summary>Where the negotiation stands.</summary>
    public RfqState State { get; init; }

    /// <summary>Unix seconds of the last change.</summary>
    public long UpdatedAt { get; init; }

    /// <summary>Corridor-specific receipts.</summary>
    public TProfile? Profile { get; init; }
}
