namespace NArk.ArkadeIntents.Rfq.Profiles.Lightning;

/// <summary>
/// Quote fields of the Lightning receive profile. Compare-only, with one exception:
/// <see cref="Invoice"/> is the thing the client actually acts on.
/// </summary>
public sealed class LightningReceiveQuoteProfile
{
    /// <summary>The payment hash, echoed back from the request.</summary>
    public string? PaymentHash { get; init; }

    /// <summary>
    /// The hold invoice the solver minted against the client's payment hash — hand this to the
    /// payer. It is held, not settled, until the client's Arkade claim reveals the preimage.
    /// </summary>
    /// <remarks>
    /// Verify it locally before publishing it: the amount must match the quote, and the payment hash
    /// must be the one sent in the request. The invoice arriving over the wire does not make it
    /// trustworthy — it makes it checkable.
    /// </remarks>
    public string? Invoice { get; init; }

    /// <summary>
    /// The solver's derivation of the funding contract's address. Compare-only: the solver funds
    /// this one, so a mismatch against the local reconstruction means the claim would target the
    /// wrong script.
    /// </summary>
    public string? LockupAddress { get; init; }

    /// <summary>
    /// The solver's own refund destination as a P2TR scriptPubKey (hex), which the covenant's
    /// <c>nonInteractiveRefund</c> leaf pins its payout to.
    /// </summary>
    /// <remarks>
    /// Needed as an input to the local reconstruction — every leaf feeds the merkle root — which is
    /// the receive leg's counterpart to the send leg's <c>receiver_pk_script</c>.
    /// </remarks>
    public string? SolverRefundPkScript { get; init; }
}
