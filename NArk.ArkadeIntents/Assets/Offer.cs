using NArk.Core.Assets;
using NBitcoin;

namespace NArk.ArkadeIntents.Assets;

/// <summary>
/// An Arkade standing order — the maker's terms for a non-interactive covenant swap. Travels inside
/// the funding tx as an Extension packet (<see cref="Services.OfferCodec.OfferPacketType"/>) so the
/// solver can discover and fill it from the txid alone. Mirrors the arkade wallet's <c>Offer</c>.
/// </summary>
public sealed class Offer
{
    /// <summary>The scriptPubKey of the swap covenant contract (computed from the other fields + the server key).</summary>
    public required byte[] SwapPkScript { get; set; }

    /// <summary>Amount the maker wants — asset units when wanting an asset, sats when wanting BTC.</summary>
    public required long WantAmount { get; set; }

    /// <summary>The asset the maker wants (BTC→asset). Null when wanting BTC.</summary>
    public AssetId? WantAsset { get; set; }

    /// <summary>The asset the maker deposits (asset→BTC). Null when depositing BTC.</summary>
    public AssetId? OfferAsset { get; set; }

    /// <summary>Maker's taproot scriptPubKey (34 bytes) — where the fill must pay.</summary>
    public required byte[] MakerPkScript { get; set; }

    /// <summary>Maker's x-only key (32 bytes) — the cancel path's <c>user</c> signer.</summary>
    public required byte[] MakerPublicKey { get; set; }

    /// <summary>Covenant co-signer (emulator) x-only key (32 bytes).</summary>
    public required byte[] EmulatorPubkey { get; set; }

    /// <summary>Partial-fill numerator. Set with <see cref="RatioDen"/> or not at all.</summary>
    public ulong? RatioNum { get; set; }

    /// <summary>Partial-fill denominator. Set with <see cref="RatioNum"/> or not at all.</summary>
    public ulong? RatioDen { get; set; }

    /// <summary>
    /// The maker's unilateral exit delay. When set, the covenant carries a third leaf spendable by
    /// the maker alone once it matures — the only path needing neither solver nor server.
    /// </summary>
    public Sequence? ExitDelay { get; set; }
}
