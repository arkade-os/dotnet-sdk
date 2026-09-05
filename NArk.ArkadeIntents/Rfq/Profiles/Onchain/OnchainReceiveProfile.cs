using NArk.ArkadeIntents.Rfq;

namespace NArk.ArkadeIntents.Rfq.Profiles.Onchain;

/// <summary>
/// The <c>onchain:BTC-&gt;arkade:BTC</c> profile: on-board Bitcoin L1 sats into an Arkade balance.
/// </summary>
/// <remarks>
/// <para>
/// The mirror of <see cref="OnchainSendProfile"/>, and the exposure mirrors with it. There the
/// client funded Arkade first and was repaid on L1; here the client funds the L1 HTLC first and the
/// <em>solver</em> funds the Arkade lockup against it — so the solver is the one paying out ahead of
/// being paid, and it collects only when the client's Arkade claim publishes the preimage.
/// </para>
/// <para>
/// The client still chooses the secret, for the same reason it does on the Lightning receive leg:
/// whoever is owed the second leg must not be able to release the first on its own. So the client
/// picks <c>P</c>, funds L1 against <c>sha256(P)</c>, claims the Arkade lockup with it, and that
/// claim is what lets the solver take the L1 side.
/// </para>
/// <para>
/// The deadlines invert with the funding order. On the send leg the client's Arkade refund had to
/// open last; here the <em>solver's</em> Arkade refund must open <em>first</em>, before the client's
/// L1 refund leaf — see <c>OnchainReceiveGates</c> for what the client checks before it funds.
/// </para>
/// </remarks>
public static class OnchainReceiveProfile
{
    /// <summary>The pair this profile negotiates.</summary>
    public const string Pair = "onchain:BTC->arkade:BTC";

    /// <summary>
    /// Build an on-board request.
    /// </summary>
    /// <param name="amountSats">The size being traded, on the leg <paramref name="amountSide"/> names.</param>
    /// <param name="amountSide">
    /// Which leg <paramref name="amountSats"/> pins: <see cref="RfqAmountSide.From"/> for what the
    /// client sends on L1, <see cref="RfqAmountSide.To"/> for what lands on Arkade.
    /// </param>
    /// <param name="paymentHash">SHA-256 of the client's own preimage (hex).</param>
    /// <param name="claimPacket">That preimage sealed to covclaimd, base64.</param>
    /// <param name="refundPubkey">The client's x-only key (hex) on the L1 HTLC's refund leaf.</param>
    /// <param name="payoutAddress">The client's Arkade address the lockup must pay.</param>
    /// <param name="payoutPubkey">The client's x-only Arkade key (hex) — the covenant's claiming role.</param>
    /// <param name="rfqId">The correlation id; a fresh one is generated when omitted.</param>
    /// <returns>The request payload, ready for a transport.</returns>
    public static RfqRequest<OnchainReceiveRequestProfile> Request(
        long amountSats,
        RfqAmountSide amountSide,
        string paymentHash,
        string claimPacket,
        string refundPubkey,
        string payoutAddress,
        string payoutPubkey,
        string? rfqId = null) => new()
    {
        RfqId = rfqId ?? RfqProtocol.NewRfqId(),
        Pair = Pair,
        AmountSide = amountSide,
        Amount = amountSats,
        Profile = new OnchainReceiveRequestProfile
        {
            PaymentHash = paymentHash,
            ClaimPacket = claimPacket,
            RefundPubkey = refundPubkey,
            PayoutAddress = payoutAddress,
            PayoutPubkey = payoutPubkey,
        },
    };
}
