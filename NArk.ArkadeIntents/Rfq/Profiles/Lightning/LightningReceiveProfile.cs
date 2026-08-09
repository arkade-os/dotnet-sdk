namespace NArk.ArkadeIntents.Rfq.Profiles.Lightning;

/// <summary>
/// The <c>lightning:BTC-&gt;arkade:BTC</c> profile: be paid over Lightning and receive the sats on
/// Arkade.
/// </summary>
/// <remarks>
/// <para>
/// The mirror image of <see cref="LightningSendProfile"/>, and the roles invert with it. Here the
/// <em>solver</em> is the one exposed: it funds the Arkade side before the Lightning payment it is
/// owed has settled, because settlement only happens when the client's claim reveals the preimage.
/// That is also why the client, not the solver, chooses the preimage.
/// </para>
/// <para>
/// The solver mints the invoice, so nothing implies the amount the way a client-supplied BOLT11
/// does on the send leg — it is always stated explicitly.
/// </para>
/// </remarks>
public static class LightningReceiveProfile
{
    /// <summary>The pair this profile negotiates.</summary>
    public const string Pair = "lightning:BTC->arkade:BTC";

    /// <summary>
    /// Build a receive-leg request.
    /// </summary>
    /// <param name="amountSats">What the client wants to receive on Arkade, in sats.</param>
    /// <param name="paymentHash">SHA-256 of the client's own preimage (hex).</param>
    /// <param name="payoutAddress">The client's Arkade address to be paid at.</param>
    /// <param name="payoutPubkey">The client's x-only key (hex) — the claiming key on this leg.</param>
    /// <param name="claimPacket">The preimage sealed to covclaimd, base64.</param>
    /// <param name="rfqId">The correlation id; a fresh one is generated when omitted.</param>
    /// <returns>The request payload, ready for a transport.</returns>
    public static RfqRequest<LightningReceiveRequestProfile> Request(
        long amountSats,
        string paymentHash,
        string payoutAddress,
        string payoutPubkey,
        string claimPacket,
        string? rfqId = null) => new()
    {
        RfqId = rfqId ?? RfqProtocol.NewRfqId(),
        Pair = Pair,
        AmountSide = RfqAmountSide.To,
        Amount = amountSats,
        Profile = new LightningReceiveRequestProfile
        {
            PaymentHash = paymentHash,
            PayoutAddress = payoutAddress,
            PayoutPubkey = payoutPubkey,
            ClaimPacket = claimPacket,
        },
    };
}
