namespace NArk.ArkadeIntents.Rfq.Profiles.Onchain;

/// <summary>
/// Quote fields of the onchain receive profile — compare-only, never trusted.
/// </summary>
/// <remarks>
/// Two contracts on two rails again, but the funding order is the mirror of the send leg's: the
/// client funds the L1 HTLC first and the solver funds the Arkade lockup against it. So of the two
/// addresses quoted here, <see cref="HtlcAddress"/> is the one the client actually pays into, and
/// <see cref="LockupAddress"/> is the one it will later claim from. Both are rebuilt locally and the
/// solver's rendering of either is only ever compared against ours.
/// </remarks>
public sealed class OnchainReceiveQuoteProfile
{
    /// <summary>The payment hash, echoed back from the request.</summary>
    public string? PaymentHash { get; init; }

    /// <summary>
    /// The solver's x-only key (hex) on the L1 HTLC's claim leaf — what it spends with once the
    /// client's Arkade claim has published the preimage.
    /// </summary>
    /// <remarks>
    /// The role-reversed counterpart of the send leg's <c>htlc_pubkey</c>: there the solver held the
    /// L1 refund role, here it holds the claim role.
    /// </remarks>
    public string? ClaimPubkey { get; init; }

    /// <summary>The L1 HTLC's absolute refund locktime, unix seconds — when the client's own way out opens.</summary>
    /// <remarks>
    /// Must mature <em>after</em> the solver's Arkade refund, which is the inverse of the send leg's
    /// ordering and follows from the inverted funding order — see <c>OnchainReceiveGates</c>.
    /// </remarks>
    public long? HtlcLocktime { get; init; }

    /// <summary>How many confirmations the client's L1 funding needs before the solver will act on it.</summary>
    public int? MinConfirmations { get; init; }

    /// <summary>
    /// The solver's derivation of the Arkade covenant's address. Compare-only — the solver funds
    /// this one, so a mismatch against the local reconstruction means the claim would target the
    /// wrong script.
    /// </summary>
    public string? LockupAddress { get; init; }

    /// <summary>The solver's derivation of the L1 HTLC address. Compare-only — and this is the one the client funds.</summary>
    public string? HtlcAddress { get; init; }

    /// <summary>
    /// The solver's own refund destination as a P2TR scriptPubKey (hex), which the Arkade covenant's
    /// <c>nonInteractiveRefund</c> leaf pins its payout to.
    /// </summary>
    /// <remarks>
    /// Compare-only, but also an input: every leaf feeds the merkle root, so the local
    /// reconstruction needs this exact value to reach a matching address. The same role the Lightning
    /// receive leg's <c>solver_refund_pk_script</c> plays, for the same reason — on a leg the solver
    /// funds, its refund destination is the one covenant parameter nothing else on the wire fixes.
    /// </remarks>
    public string? SolverRefundPkScript { get; init; }
}
