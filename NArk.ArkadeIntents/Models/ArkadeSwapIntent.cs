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

    /// <summary>Hex-encoded offer TLV — rebuilds the covenant contract for the cancel path.</summary>
    public required string OfferHex { get; set; }

    /// <summary>
    /// The maker's signing output descriptor — the wallet-spendable form of the cancel path's
    /// <c>$user</c> key. The offer only carries the x-only key (enough for the covenant/address); the
    /// full descriptor is kept locally so the cancel spend is actually signable.
    /// </summary>
    public string? MakerDescriptor { get; set; }

    /// <summary>Asset id deposited (<c>"btc"</c> for BTC).</summary>
    public string? FromAssetId { get; set; }

    /// <summary>Asset id received.</summary>
    public string? ToAssetId { get; set; }


    /// <summary>
    /// The BOLT11 this swap pays (<see cref="ArkadeSwapIntentType.BtcToLightning"/> only). Kept because
    /// it is unrecoverable from anything else: the covenant commits to <c>ripemd160(sha256(P))</c>,
    /// which is one-way and is not even the invoice's own payment hash.
    /// </summary>
    public string? Invoice { get; set; }

    /// <summary>The invoice's payment hash (hex) — the natural key a solver dedupes a negotiation on.</summary>
    public string? PaymentHash { get; set; }

    /// <summary>
    /// Unix seconds at which the covenant refund path opens (<see cref="ArkadeSwapIntentType.BtcToLightning"/>
    /// only). The monitor needs it to tell a spend that can only be a fill from one that might be a
    /// refund.
    /// </summary>
    public long? RefundLocktime { get; set; }

    /// <summary>
    /// The preimage this swap settles on, hex (<see cref="ArkadeSwapIntentType.LightningToBtc"/>
    /// only) — the one piece of state that cannot be re-derived from anything else.
    /// </summary>
    /// <remarks>
    /// We chose it, so nobody else holds it in the clear; the sealed copy travelling to covclaimd is
    /// a fallback claimer, not a backup we can read. Losing this row before the claim lands means
    /// waiting out the solver's reclaim and getting nothing.
    /// </remarks>
    public string? Preimage { get; set; }

    /// <summary>
    /// The counterparty's x-only key on the L1 HTLC's refund leaf, hex
    /// (<see cref="ArkadeSwapIntentType.BtcToOnchain"/> only).
    /// </summary>
    /// <remarks>
    /// This and <see cref="HtlcLocktime"/> are the only parts of the L1 leg that are nobody's to
    /// re-derive: everything else about that contract comes from this row's payment hash and the
    /// wallet's own key. The address is deliberately NOT stored — recomputing it from these keeps a
    /// derived value from drifting away from what derived it.
    /// </remarks>
    public string? HtlcPubkey { get; set; }

    /// <summary>
    /// Unix seconds at which the L1 HTLC's refund leaf opens for the counterparty
    /// (<see cref="ArkadeSwapIntentType.BtcToOnchain"/> only).
    /// </summary>
    /// <remarks>
    /// Always earlier than <see cref="RefundLocktime"/>, and by a margin — the ordering the corridor
    /// refuses to fund without.
    /// </remarks>
    public long? HtlcLocktime { get; set; }

    /// <summary>
    /// Where the off-board pays out on Bitcoin L1 (<see cref="ArkadeSwapIntentType.BtcToOnchain"/>
    /// only) — the address the client asked to be paid at.
    /// </summary>
    /// <remarks>
    /// The claim chooses this destination, not the HTLC, so it is not committed to by either
    /// contract and has to be remembered. A swap whose row is lost can still be claimed once
    /// rebuilt, but the sats would land wherever that rebuild names.
    /// </remarks>
    public string? OnchainPayoutAddress { get; set; }

    /// <summary>The ark tx that fulfilled the swap (spent the covenant VTXO); set once <see cref="ArkadeSwapIntentStatus.Fulfilled"/>.</summary>
    public string? SpentTxid { get; set; }
}
