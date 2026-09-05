using NArk.ArkadeIntents.Services;
using NBitcoin;

namespace NArk.ArkadeIntents.Models;

public class ArkadeSwapIntent
{
    /// <summary>
    /// Identity — the funding txid for an asset swap, the RFQ correlation id for a Lightning one.
    /// </summary>
    public required string Id { get; set; }

    /// <summary>The wallet that owns this swap.</summary>
    public required string WalletId { get; set; }

    public required ArkadeSwapIntentType Type { get; set; }

    public required Money OfferAmount { get; set; }
    public required Money WantAmount { get; set; }

    public required ArkadeSwapIntentStatus Status { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Hex pkScript of the swap covenant contract — the key the monitor watches. VTXO changes on
    /// this script drive the intent's status (see <see cref="ArkadeSwapIntentMonitoringService"/>).
    /// </summary>
    public required string SwapPkScript { get; set; }

    /// <summary>The swap covenant's Arkade address (the funding target).</summary>
    public required string SwapAddress { get; set; }

    /// <summary>
    /// Everything this swap keeps that only its own corridor understands — the offer TLV of an asset
    /// swap, the BOLT11 of a Lightning one, the L1 HTLC's terms on an off-board.
    /// </summary>
    /// <remarks>
    /// A blob rather than a column each, because a column each makes the table a union of every
    /// corridor that has ever existed: eight nullable fields of which any one row uses three, and a
    /// schema migration every time a corridor is added. Read and write it through the typed views in
    /// <see cref="ArkadeSwapIntentMetadataExtensions"/> rather than by key, so a corridor's shape is
    /// stated once instead of at each call site.
    /// </remarks>
    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>Asset id deposited (<c>"btc"</c> for BTC).</summary>
    public string? FromAssetId { get; set; }

    /// <summary>Asset id received.</summary>
    public string? ToAssetId { get; set; }


    /// <summary>The invoice's payment hash (hex) — the natural key a solver dedupes a negotiation on.</summary>
    public string? PaymentHash { get; set; }

    /// <summary>
    /// Unix seconds at which the covenant refund path opens (<see cref="ArkadeSwapIntentType.BtcToLightning"/>
    /// only). The monitor needs it to tell a spend that can only be a fill from one that might be a
    /// refund.
    /// </summary>
    public long? RefundLocktime { get; set; }

    /// <summary>The ark tx that fulfilled the swap (spent the covenant VTXO); set once <see cref="ArkadeSwapIntentStatus.Fulfilled"/>.</summary>
    public string? SpentTxid { get; set; }
}
