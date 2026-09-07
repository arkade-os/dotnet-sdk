namespace NArk.ArkadeIntents.Rfq.Profiles.Onchain;

/// <summary>Request fields of the onchain receive profile.</summary>
/// <remarks>
/// <para>
/// Roles reverse from <see cref="OnchainSendRequestProfile"/>, and the field names follow the roles
/// rather than the direction. There the client's <c>payout_pubkey</c> was its L1 <em>claim</em> key;
/// here the client holds the L1 HTLC's <em>refund</em> role, so the same contribution is
/// <see cref="RefundPubkey"/>.
/// </para>
/// <para>
/// There is no Arkade refund address, and its absence is the corridor rather than an omission: on
/// this leg the client never funds the Arkade side at all, so it has nothing to be refunded from
/// there. Its recourse is the L1 HTLC's own refund leaf.
/// </para>
/// </remarks>
public sealed class OnchainReceiveRequestProfile
{
    /// <summary>
    /// SHA-256 of the client's own preimage (32 bytes, hex) — the secret both rails turn on.
    /// </summary>
    /// <remarks>
    /// The client chooses it for the same reason it does on the Lightning receive leg: the solver
    /// funds Arkade before it has been paid on L1, and it collects only once the client's Arkade
    /// claim publishes the secret. A solver holding that secret would be paid for a swap it never
    /// delivered.
    /// </remarks>
    public required string PaymentHash { get; init; }

    /// <summary>
    /// The preimage, ECIES-sealed to covclaimd (<c>ephPub(33) || nonce(12) || ciphertext</c>,
    /// base64), so the Arkade claim can be pushed while the client is offline.
    /// </summary>
    /// <remarks>
    /// Opaque to the solver, which never holds the key that opens it and validates only its shape.
    /// A client willing to stay online for the claim may send any well-formed filler here.
    /// </remarks>
    public required string ClaimPacket { get; init; }

    /// <summary>
    /// The client's x-only key (32 bytes, hex) on the L1 HTLC's refund leaf — the only way back if
    /// the solver never funds the Arkade side.
    /// </summary>
    public required string RefundPubkey { get; init; }

    /// <summary>The client's Arkade address, where the solver's lockup must pay.</summary>
    public required string PayoutAddress { get; init; }

    /// <summary>
    /// The client's x-only Arkade key (32 bytes, hex) — the covenant's <c>receiver</c> role, and so
    /// the key that claims.
    /// </summary>
    /// <remarks>
    /// Sent even though covclaimd can push the claim on the client's behalf, and deliberately: a
    /// covenant the client cannot spend itself makes covclaimd a hard dependency of the corridor,
    /// and a covclaimd that accepts a reveal and then never claims would leave the swap with no
    /// working claim path at all.
    /// </remarks>
    public required string PayoutPubkey { get; init; }
}
