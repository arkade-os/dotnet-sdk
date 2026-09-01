namespace NArk.ArkadeIntents.Models;

/// <summary>
/// The keys <see cref="ArkadeSwapIntent.Metadata"/> is written under. Constants rather than string
/// literals at each call site, so a typo is a build error instead of a value that silently reads
/// back as absent.
/// </summary>
public static class ArkadeSwapMetadataKeys
{
    /// <summary>Hex-encoded offer TLV — rebuilds an asset swap's covenant for the cancel path.</summary>
    public const string OfferHex = "offerHex";

    /// <summary>The maker's signing output descriptor for an asset swap's cancel path.</summary>
    public const string MakerDescriptor = "makerDescriptor";

    /// <summary>The BOLT11 a Lightning swap pays or is paid by.</summary>
    public const string Invoice = "invoice";

    /// <summary>The preimage a swap settles on, hex.</summary>
    public const string Preimage = "preimage";

    /// <summary>The counterparty's x-only key on an off-board's L1 HTLC refund leaf, hex.</summary>
    public const string HtlcPubkey = "htlcPubkey";

    /// <summary>Unix seconds at which that refund leaf opens.</summary>
    public const string HtlcLocktime = "htlcLocktime";

    /// <summary>Where an off-board pays out on Bitcoin L1.</summary>
    public const string OnchainPayoutAddress = "onchainPayoutAddress";
}

/// <summary>What an Arkade BTC↔asset swap keeps beyond the fields every corridor has.</summary>
/// <param name="OfferHex">Hex-encoded offer TLV — the only way back to the covenant for a cancel.</param>
/// <param name="MakerDescriptor">
/// The maker's signing descriptor. The offer carries only the x-only key, which is enough for the
/// address but not enough to sign, so the spendable form is kept locally or the cancel cannot be
/// built.
/// </param>
public sealed record AssetSwapMetadata(string OfferHex, string? MakerDescriptor);

/// <summary>What a Lightning swap keeps beyond the fields every corridor has.</summary>
/// <param name="Invoice">
/// The BOLT11. Kept because it is unrecoverable from anything else: the covenant commits to
/// <c>ripemd160(sha256(P))</c>, which is one-way and is not even the invoice's own payment hash.
/// </param>
/// <param name="Preimage">
/// The secret a receive swap settles on, hex — <c>null</c> on a send, where the solver holds it.
/// A cache rather than the source of truth: we derive it from the wallet's own seed, so a lost row
/// is recoverable (see <c>PreimageProvisioning</c>).
/// </param>
public sealed record LightningSwapMetadata(string? Invoice, string? Preimage);

/// <summary>What an off-board keeps beyond the fields every corridor has.</summary>
/// <param name="Preimage">The secret linking the two rails, hex. Derived from the wallet's seed.</param>
/// <param name="HtlcPubkey">
/// The counterparty's x-only key on the L1 HTLC's refund leaf. This and <paramref name="HtlcLocktime"/>
/// are the only parts of the L1 leg nobody can re-derive — everything else about that contract comes
/// from the payment hash and the wallet's own key. The address is deliberately not kept: recomputing
/// it stops a derived value from drifting away from what derived it.
/// </param>
/// <param name="HtlcLocktime">
/// Unix seconds at which that leaf opens for the counterparty. Always earlier than the Arkade side's
/// refund locktime, and by a margin — the ordering the corridor refuses to fund without.
/// </param>
/// <param name="PayoutAddress">
/// Where the off-board pays out on L1. The claim chooses this, not the HTLC, so neither contract
/// commits to it and it has to be remembered: a swap whose row is lost can still be claimed once
/// rebuilt, but the sats land wherever that rebuild names.
/// </param>
public sealed record OnchainSwapMetadata(
    string? Preimage, string? HtlcPubkey, long? HtlcLocktime, string? PayoutAddress);

/// <summary>
/// Typed views over <see cref="ArkadeSwapIntent.Metadata"/> — one per corridor, so the blob is read
/// and written through a shape rather than through string keys at each call site.
/// </summary>
/// <remarks>
/// Each reader checks the intent's <see cref="ArkadeSwapIntent.Type"/> first. Reading an off-board's
/// keys off a Lightning swap is not an empty result, it is the wrong question, and answering it with
/// a record full of nulls turns a mistake at the call site into a `NullReferenceException` several
/// frames away.
/// </remarks>
public static class ArkadeSwapIntentMetadataExtensions
{
    /// <summary>Read the asset-swap view.</summary>
    /// <exception cref="InvalidOperationException">This intent is not an asset swap.</exception>
    /// <exception cref="InvalidOperationException">The row carries no offer, so the cancel path is unreachable.</exception>
    public static AssetSwapMetadata AssetMetadata(this ArkadeSwapIntent intent)
    {
        Require(intent, ArkadeSwapIntentType.BtcToAsset, ArkadeSwapIntentType.AssetToBtc);
        return new AssetSwapMetadata(
            Get(intent, ArkadeSwapMetadataKeys.OfferHex)
                ?? throw new InvalidOperationException(
                    $"asset swap '{intent.Id}' carries no offer, so its covenant cannot be rebuilt"),
            Get(intent, ArkadeSwapMetadataKeys.MakerDescriptor));
    }

    /// <summary>Write the asset-swap view, replacing whatever those keys held.</summary>
    public static ArkadeSwapIntent WithAssetMetadata(this ArkadeSwapIntent intent, AssetSwapMetadata metadata)
    {
        Set(intent, ArkadeSwapMetadataKeys.OfferHex, metadata.OfferHex);
        Set(intent, ArkadeSwapMetadataKeys.MakerDescriptor, metadata.MakerDescriptor);
        return intent;
    }

    /// <summary>Read the Lightning view.</summary>
    /// <exception cref="InvalidOperationException">This intent is not a Lightning swap.</exception>
    public static LightningSwapMetadata LightningMetadata(this ArkadeSwapIntent intent)
    {
        Require(intent, ArkadeSwapIntentType.BtcToLightning, ArkadeSwapIntentType.LightningToBtc);
        return new LightningSwapMetadata(
            Get(intent, ArkadeSwapMetadataKeys.Invoice),
            Get(intent, ArkadeSwapMetadataKeys.Preimage));
    }

    /// <summary>Write the Lightning view, replacing whatever those keys held.</summary>
    public static ArkadeSwapIntent WithLightningMetadata(
        this ArkadeSwapIntent intent, LightningSwapMetadata metadata)
    {
        Set(intent, ArkadeSwapMetadataKeys.Invoice, metadata.Invoice);
        Set(intent, ArkadeSwapMetadataKeys.Preimage, metadata.Preimage);
        return intent;
    }

    /// <summary>Read the off-board view.</summary>
    /// <exception cref="InvalidOperationException">This intent is not an off-board.</exception>
    public static OnchainSwapMetadata OnchainMetadata(this ArkadeSwapIntent intent)
    {
        Require(intent, ArkadeSwapIntentType.BtcToOnchain);
        return new OnchainSwapMetadata(
            Get(intent, ArkadeSwapMetadataKeys.Preimage),
            Get(intent, ArkadeSwapMetadataKeys.HtlcPubkey),
            Get(intent, ArkadeSwapMetadataKeys.HtlcLocktime) is { } locktime
                ? long.Parse(locktime)
                : null,
            Get(intent, ArkadeSwapMetadataKeys.OnchainPayoutAddress));
    }

    /// <summary>Write the off-board view, replacing whatever those keys held.</summary>
    public static ArkadeSwapIntent WithOnchainMetadata(
        this ArkadeSwapIntent intent, OnchainSwapMetadata metadata)
    {
        Set(intent, ArkadeSwapMetadataKeys.Preimage, metadata.Preimage);
        Set(intent, ArkadeSwapMetadataKeys.HtlcPubkey, metadata.HtlcPubkey);
        Set(intent, ArkadeSwapMetadataKeys.HtlcLocktime, metadata.HtlcLocktime?.ToString());
        Set(intent, ArkadeSwapMetadataKeys.OnchainPayoutAddress, metadata.PayoutAddress);
        return intent;
    }

    private static string? Get(ArkadeSwapIntent intent, string key) =>
        intent.Metadata.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    // A null writes nothing rather than an empty string: "the corridor has no such value" and "the
    // corridor has an empty one" are different, and only the first should read back as absent.
    private static void Set(ArkadeSwapIntent intent, string key, string? value)
    {
        if (value is null)
        {
            intent.Metadata.Remove(key);
            return;
        }
        intent.Metadata[key] = value;
    }

    private static void Require(ArkadeSwapIntent intent, params ArkadeSwapIntentType[] types)
    {
        if (!types.Contains(intent.Type))
        {
            throw new InvalidOperationException(
                $"swap '{intent.Id}' is a {intent.Type}, so it carries no " +
                $"{string.Join("/", types)} metadata");
        }
    }
}
