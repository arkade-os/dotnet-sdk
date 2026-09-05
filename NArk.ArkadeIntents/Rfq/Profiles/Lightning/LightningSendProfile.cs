namespace NArk.ArkadeIntents.Rfq.Profiles.Lightning;

/// <summary>
/// The <c>arkade:BTC-&gt;lightning:BTC</c> profile: pay a BOLT11 out of an Arkade balance.
/// </summary>
/// <remarks>
/// One of several corridor profiles the RFQ envelope can carry — the negotiation layer itself knows
/// nothing about Lightning, which is why everything invoice-shaped lives here rather than there.
/// </remarks>
public static class LightningSendProfile
{
    /// <summary>The pair this profile negotiates.</summary>
    public const string Pair = "arkade:BTC->lightning:BTC";

    /// <summary>
    /// Build a send-leg request. A BOLT11 profile is always exact-out and omits the amount, so the
    /// invoice alone fixes what the solver must pay.
    /// </summary>
    /// <param name="invoice">The BOLT11 to be paid.</param>
    /// <param name="refundAddress">The client's own Arkade refund address.</param>
    /// <param name="clientRefundPubkey">
    /// The client's own x-only key (hex) for the covenant's client-side refund leaves. Keep the
    /// matching private key: it is the only recourse that depends on nobody else.
    /// </param>
    /// <param name="rfqId">The correlation id; a fresh one is generated when omitted.</param>
    /// <returns>The request payload, ready for a transport.</returns>
    public static RfqRequest<LightningSendRequestProfile> Request(
        string invoice, string refundAddress, string clientRefundPubkey, string? rfqId = null) => new()
    {
        RfqId = rfqId ?? RfqProtocol.NewRfqId(),
        Pair = Pair,
        AmountSide = RfqAmountSide.To,
        Profile = new LightningSendRequestProfile
        {
            Invoice = invoice,
            RefundAddress = refundAddress,
            ClientRefundPubkey = clientRefundPubkey,
        },
    };
}
