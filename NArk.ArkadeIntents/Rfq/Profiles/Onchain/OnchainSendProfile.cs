using NArk.ArkadeIntents.Rfq;

namespace NArk.ArkadeIntents.Rfq.Profiles.Onchain;

/// <summary>
/// The <c>arkade:BTC-&gt;onchain:BTC</c> profile: off-board an Arkade balance to Bitcoin L1.
/// </summary>
/// <remarks>
/// <para>
/// Two contracts on two rails, linked by one secret. The client funds an Arkade covenant; the solver
/// funds an L1 HTLC paying the client; the client claims on L1 by revealing the preimage, and that
/// reveal is what lets the solver take the Arkade side. Neither party is ever owed anything on trust.
/// </para>
/// <para>
/// The client picks the preimage because the client moves first. Funding Arkade before the solver
/// has funded L1 is the exposed position, and holding the secret is what makes it safe: nothing the
/// solver does can take the Arkade side until the client has been paid on L1.
/// </para>
/// </remarks>
public static class OnchainSendProfile
{
    /// <summary>The pair this profile negotiates.</summary>
    public const string Pair = "arkade:BTC->onchain:BTC";

    /// <summary>
    /// Build an off-board request.
    /// </summary>
    /// <param name="amountSats">The size being traded, on the leg <paramref name="amountSide"/> names.</param>
    /// <param name="amountSide">
    /// Which leg <paramref name="amountSats"/> pins: <see cref="RfqAmountSide.To"/> for what lands
    /// on L1, <see cref="RfqAmountSide.From"/> for what leaves the Arkade balance.
    /// </param>
    /// <param name="paymentHash">SHA-256 of the client's own preimage (hex).</param>
    /// <param name="payoutPubkey">The client's x-only key (hex) that claims the L1 HTLC.</param>
    /// <param name="refundAddress">The client's Arkade address the covenant refunds to.</param>
    /// <param name="clientRefundPubkey">The client's x-only key (hex) for the covenant's refund leaves.</param>
    /// <param name="rfqId">The correlation id; a fresh one is generated when omitted.</param>
    /// <returns>The request payload, ready for a transport.</returns>
    public static RfqRequest<OnchainSendRequestProfile> Request(
        long amountSats,
        RfqAmountSide amountSide,
        string paymentHash,
        string payoutPubkey,
        string refundAddress,
        string clientRefundPubkey,
        string? rfqId = null) => new()
    {
        RfqId = rfqId ?? RfqProtocol.NewRfqId(),
        Pair = Pair,
        AmountSide = amountSide,
        Amount = amountSats,
        Profile = new OnchainSendRequestProfile
        {
            PaymentHash = paymentHash,
            PayoutPubkey = payoutPubkey,
            RefundAddress = refundAddress,
            ClientRefundPubkey = clientRefundPubkey,
        },
    };
}
