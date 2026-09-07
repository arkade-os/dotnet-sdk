namespace NArk.ArkadeIntents.Rfq.Profiles.Onchain;

/// <summary>Status fields the solver reports for an onchain receive swap.</summary>
/// <remarks>
/// Advisory throughout. What actually happened is read off the two chains — the client's L1 HTLC and
/// the Arkade covenant's VTXO — never from the party with an interest in the answer.
/// </remarks>
public sealed class OnchainReceiveStatusProfile
{
    /// <summary>The payment hash, echoed back.</summary>
    public string? PaymentHash { get; init; }

    /// <summary>The Arkade covenant's address.</summary>
    public string? LockupAddress { get; init; }

    /// <summary>The L1 HTLC's address.</summary>
    public string? HtlcAddress { get; init; }

    /// <summary>The client's transaction that funded the L1 HTLC, once the solver has seen it.</summary>
    public string? FundingTxid { get; init; }

    /// <summary>The transaction that claimed the Arkade lockup — the client's own, or covclaimd's for it.</summary>
    public string? ArkadeClaimTxid { get; init; }

    /// <summary>The solver's L1 claim, which is what ends the swap from its side.</summary>
    public string? SettleTxid { get; init; }

    /// <summary>The solver reclaiming its own Arkade lockup, when the swap failed.</summary>
    public string? RefundTxid { get; init; }

    /// <summary>Why the solver refused or abandoned the swap. Never branch on it.</summary>
    public string? FailureReason { get; init; }

    /// <summary>
    /// The preimage, published only once settlement itself did.
    /// </summary>
    /// <remarks>
    /// A receipt rather than a secret by that point, and the client already holds it — it chose it.
    /// Useful only for reconciling against what the solver believes happened.
    /// </remarks>
    public string? Preimage { get; init; }
}
