namespace NArk.ArkadeIntents.Rfq.Profiles.Lightning;

/// <summary>Status fields of the Lightning send profile.</summary>
public sealed class LightningSendStatusProfile
{
    /// <summary>The invoice's payment hash.</summary>
    public string? PaymentHash { get; init; }

    /// <summary>The swap contract's address as the solver derived it.</summary>
    public string? LockupAddress { get; init; }

    /// <summary>The txid of the solver's claim, once it has claimed.</summary>
    public string? ClaimTxid { get; init; }

    /// <summary>The txid of the covenant refund, if one was pushed.</summary>
    public string? RefundTxid { get; init; }

    /// <summary>Why the swap failed, when it did.</summary>
    public string? FailureReason { get; init; }

    /// <summary>
    /// The payment preimage — the receipt for the invoice. Present only in
    /// <see cref="RfqState.Settled"/>: before that it is the solver's leverage, and on a failed swap
    /// it never exists.
    /// </summary>
    public string? Preimage { get; init; }
}
