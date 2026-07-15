using NBitcoin;

namespace NArk.Arkade.NonInteractiveSwaps;

public class SwapIntent
{
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

    /// <summary>The ark tx that fulfilled the swap (spent the covenant VTXO); set once <see cref="SwapIntentStatus.Fulfilled"/>.</summary>
    public string? SpentTxid { get; set; }
}
