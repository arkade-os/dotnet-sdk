using System.Numerics;
using NArk.Core.Assets;

namespace NArk.ArkadeIntents.Rfq.Profiles.Asset;

/// <summary>
/// The <c>arkade:X-&gt;arkade:Y</c> profile: an Arkade-to-Arkade swap negotiated over RFQ.
/// </summary>
/// <remarks>
/// <para>
/// This is the <b>atomic</b> class, and it differs from the four HTLC corridors in what it does not
/// have. There is no hash lock, no <c>refund_locktime</c> and no refund path, because the covenant
/// the client funds only releases the deposit to a transaction that pays the quoted amount to the
/// client's own script in the same transaction. The solver fills or nothing moves; an offer that is
/// never filled is reclaimed cooperatively by cancelling it.
/// </para>
/// <para>
/// The only clock is <c>valid_until</c>. Funding past it is a deposit the solver will decline to
/// fill, recoverable only by that cancel — which is why this profile's gate checks a clock and
/// nothing else.
/// </para>
/// <para>
/// Amounts travel as canonical decimal strings, not JSON numbers: one leg is always an Arkade asset
/// whose atomic unit is 256-bit, and a whole unit of an 18-decimal asset is a hundred times what a
/// double represents exactly.
/// </para>
/// </remarks>
public static class ArkadeSwapProfile
{
    /// <summary>The leg spelling for Arkade sats.</summary>
    public const string BtcLeg = "arkade:BTC";

    /// <summary>The leg spelling for an Arkade-issued asset.</summary>
    /// <param name="asset">The asset.</param>
    /// <returns><c>arkade:</c> followed by the asset's 68-character hex id.</returns>
    public static string Leg(AssetId asset) => $"arkade:{asset}";

    /// <summary>The pair string for a swap between two Arkade legs.</summary>
    /// <param name="offerAsset">The asset deposited, or <c>null</c> to deposit sats.</param>
    /// <param name="wantAsset">The asset wanted, or <c>null</c> to want sats.</param>
    /// <returns>The pair, as the solver spells it.</returns>
    /// <exception cref="ArgumentException">Neither leg names an asset.</exception>
    public static string Pair(AssetId? offerAsset, AssetId? wantAsset)
    {
        if (offerAsset is null && wantAsset is null)
        {
            throw new ArgumentException(
                "name an asset on at least one leg — with neither set both legs are sats, which is " +
                "not a swap", nameof(offerAsset));
        }
        return $"{(offerAsset is null ? BtcLeg : Leg(offerAsset))}->{(wantAsset is null ? BtcLeg : Leg(wantAsset))}";
    }

    /// <summary>
    /// Build the request. The pair names the assets; the profile names the client.
    /// </summary>
    /// <param name="offerAsset">The asset deposited, or <c>null</c> to deposit sats.</param>
    /// <param name="wantAsset">The asset wanted, or <c>null</c> to want sats.</param>
    /// <param name="amount">Atomic units of whichever leg <paramref name="amountSide"/> names.</param>
    /// <param name="amountSide">Which leg <paramref name="amount"/> fixes.</param>
    /// <param name="makerPkScript">The client's taproot scriptPubKey, 34 bytes.</param>
    /// <param name="makerPublicKey">The client's x-only key, 32 bytes.</param>
    /// <param name="rfqId">A correlation id, or <c>null</c> to draw a fresh one.</param>
    /// <returns>The request.</returns>
    /// <remarks>
    /// Unlike the Lightning send profile, where a BOLT11 carries the amount, nothing here implies
    /// one: there is no invoice, and the offer that would state it does not exist until this quote
    /// has been answered. So <paramref name="amount"/> is always required, on whichever side.
    /// </remarks>
    /// <exception cref="ArgumentException">A parameter is not the shape the covenant needs.</exception>
    public static RfqRequest<ArkadeSwapRequestProfile> Request(
        AssetId? offerAsset,
        AssetId? wantAsset,
        BigInteger amount,
        RfqAmountSide amountSide,
        byte[] makerPkScript,
        byte[] makerPublicKey,
        string? rfqId = null)
    {
        if (amount <= BigInteger.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "an amount must be positive");
        }
        if (makerPkScript.Length != 34)
        {
            throw new ArgumentException(
                $"a taproot scriptPubKey is 34 bytes, got {makerPkScript.Length} — the covenant " +
                "slices the witness program out of this value, so a different length pins the fill " +
                "to an output nobody controls", nameof(makerPkScript));
        }
        if (makerPublicKey.Length != 32)
        {
            throw new ArgumentException(
                $"an x-only key is 32 bytes, got {makerPublicKey.Length}", nameof(makerPublicKey));
        }

        return new RfqRequest<ArkadeSwapRequestProfile>
        {
            RfqId = rfqId ?? RfqProtocol.NewRfqId(),
            Pair = Pair(offerAsset, wantAsset),
            AmountSide = amountSide,
            AtomicAmount = amount,
            Profile = new ArkadeSwapRequestProfile
            {
                MakerPkScript = Convert.ToHexString(makerPkScript).ToLowerInvariant(),
                MakerPublicKey = Convert.ToHexString(makerPublicKey).ToLowerInvariant(),
            },
        };
    }
}
