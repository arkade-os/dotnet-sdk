namespace NArk.ArkadeIntents.Rfq.Profiles.Lightning;

/// <summary>Request fields of the Lightning receive profile.</summary>
/// <remarks>
/// The client picks the preimage here and never sends it to the solver — only its hash, plus a
/// sealed copy the solver cannot open (<see cref="ClaimPacket"/>). That is what keeps the solver
/// from settling the Lightning side before the client has been paid on Arkade.
/// </remarks>
public sealed class LightningReceiveRequestProfile
{
    /// <summary>
    /// SHA-256 of the client's own preimage (32 bytes, hex). The solver mints its hold invoice
    /// against this hash, so the client — not the solver — decides when the swap settles.
    /// </summary>
    public required string PaymentHash { get; init; }

    /// <summary>The client's Arkade address, where the swap pays out.</summary>
    public required string PayoutAddress { get; init; }

    /// <summary>
    /// The client's x-only key (32 bytes, hex) for the covenant. On this leg the roles are inverted
    /// from the send leg: the client is the <em>receiver</em>, so this is the key that claims.
    /// </summary>
    public required string PayoutPubkey { get; init; }

    /// <summary>
    /// The preimage, ECIES-sealed to covclaimd (<c>ephPub(33) || nonce(12) || ciphertext</c>,
    /// base64), so the claim can be pushed while the client is offline.
    /// </summary>
    /// <remarks>
    /// Opaque to the solver, which never holds the key that opens it and validates only its shape.
    /// A client willing to stay online for the claim may send any well-formed filler here.
    /// </remarks>
    public required string ClaimPacket { get; init; }
}
