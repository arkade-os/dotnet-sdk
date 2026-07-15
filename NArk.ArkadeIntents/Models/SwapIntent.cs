using NArk.ArkadeIntents.Services;
using NBitcoin;

namespace NArk.ArkadeIntents.Models;

public class SwapIntent
{
    /// <summary>Identity — the funding txid that created the swap's covenant VTXO.</summary>
    public required string Id { get; set; }

    /// <summary>The wallet that owns this swap.</summary>
    public required string WalletId { get; set; }

    public required SwapIntentType Type { get; set; }

    public required Money OfferAmount { get; set; }
    public required Money WantAmount { get; set; }

    public required SwapIntentStatus Status { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Hex pkScript of the swap covenant contract — the key the monitor watches. VTXO changes on
    /// this script drive the intent's status (see <see cref="SwapIntentMonitoringService"/>).
    /// </summary>
    public required string SwapPkScript { get; set; }

    /// <summary>The swap covenant's Arkade address (the funding target).</summary>
    public required string SwapAddress { get; set; }

    /// <summary>Hex-encoded offer TLV — rebuilds the covenant contract for the cancel path.</summary>
    public required string OfferHex { get; set; }

    /// <summary>Asset id deposited (<c>"btc"</c> for BTC).</summary>
    public string? FromAssetId { get; set; }

    /// <summary>Asset id received.</summary>
    public string? ToAssetId { get; set; }

    /// <summary>The ark tx that fulfilled the swap (spent the covenant VTXO); set once <see cref="SwapIntentStatus.Fulfilled"/>.</summary>
    public string? SpentTxid { get; set; }
}
