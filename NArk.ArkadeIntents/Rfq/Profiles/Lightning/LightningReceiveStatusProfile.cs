namespace NArk.ArkadeIntents.Rfq.Profiles.Lightning;

/// <summary>Status fields of the Lightning receive profile.</summary>
public sealed class LightningReceiveStatusProfile
{
    /// <summary>The payment hash this swap is keyed by.</summary>
    public string? PaymentHash { get; init; }

    /// <summary>The funding contract's address as the solver derived it.</summary>
    public string? LockupAddress { get; init; }

    /// <summary>The txid of the solver reclaiming its own lockup, if it came to that.</summary>
    public string? RefundTxid { get; init; }

    /// <summary>Why the swap failed, when it did.</summary>
    public string? FailureReason { get; init; }

    /// <summary>
    /// The preimage, echoed back only in <see cref="RfqState.Settled"/>.
    /// </summary>
    /// <remarks>
    /// Notably absent one state earlier, at <see cref="RfqState.Filled"/>: by then the client's own
    /// claim has already made the preimage public on Arkade, but the solver has not yet been paid,
    /// so it still publishes nothing. The client learns nothing here it did not already choose.
    /// </remarks>
    public string? Preimage { get; init; }
}
