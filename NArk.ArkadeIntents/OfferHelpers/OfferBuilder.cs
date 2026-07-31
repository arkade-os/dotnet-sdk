using NArk.Abstractions.Extensions;
using NArk.Arkade.Contracts;
using NArk.Arkade.Program.Models;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Programs;
using NArk.Core.Assets;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.ArkadeIntents.Services;

/// <summary>The result of building an Arkade offer: the covenant contract plus its wire form.</summary>
public sealed record CreatedOffer(
    string OfferHex,
    byte[] Payload,
    string Address,
    byte[] SwapPkScript,
    ArkProgramContract Contract);

/// <summary>
/// Compiles an Arkade <see cref="Offer"/> into its covenant contract (and, for creation, the offer's
/// address + wire payload). Mirrors the arkade wallet's <c>offerVtxoScript</c> / <c>createOffer</c>:
/// picks the direction's program (<see cref="ArkadeIntentPrograms.BtcToAsset"/> when wanting an asset,
/// else <see cref="ArkadeIntentPrograms.AssetToBtc"/>) and binds its <c>$param</c>s from the offer.
/// </summary>
public static class OfferBuilder
{
    /// <summary>
    /// Compile the offer's covenant contract. The <paramref name="server"/> descriptor supplies the
    /// arkd signer key (the offer never stores it — cancel rebuilds against the current server, so
    /// the derived address stays identical).
    /// </summary>
    public static ArkProgramContract BuildContract(Offer offer, OutputDescriptor server, Network network)
        => BuildContract(offer, server, network, XOnlyToDescriptor(offer.MakerPublicKey, network));

    /// <summary>
    /// Compile the covenant using an explicit <paramref name="maker"/> descriptor for the cancel
    /// <c>$user</c> signer. Its x-only key must equal <see cref="Offer.MakerPublicKey"/> (so the
    /// derived address is identical to the funded one) — the point is to carry the wallet-spendable
    /// descriptor for signing, which the wire offer (x-only only) can't. Used by the cancel path.
    /// </summary>
    public static ArkProgramContract BuildContract(Offer offer, OutputDescriptor server, Network network, OutputDescriptor maker)
    {
        var program = offer.WantAsset is not null ? ArkadeIntentPrograms.BtcToAsset : ArkadeIntentPrograms.AssetToBtc;

        var args = new Dictionary<string, AsmToken>
        {
            // Drop the taproot prefix (OP_1 PUSH32) to leave the 32-byte witness program.
            ["makerWP"] = AsmToken.FromBytes(offer.MakerPkScript[2..]),
            ["wantAmount"] = AsmToken.FromNumber(offer.WantAmount),
        };
        if (offer.WantAsset is { } wantAsset)
        {
            // Internal byte order: the covenant compares the asset txid reversed.
            args["wantAssetTxid"] = AsmToken.FromBytes(wantAsset.Txid.Reverse().ToArray());
            args["wantAssetGroupIndex"] = AsmToken.FromNumber(wantAsset.GroupIndex);
        }

        var emulator = ECXOnlyPubKey.Create(offer.EmulatorPubkey);

        // $server / $user auto-bind from the server / maker descriptors.
        return new ArkProgramContract(server, program, args, maker, emulator);
    }

    /// <summary>
    /// Build a fresh offer: compile the covenant, compute its address + scriptPubKey, and serialize
    /// the wire payload. Exactly one of <paramref name="wantAsset"/> (BTC→asset) or
    /// <paramref name="offerAsset"/> (asset→BTC) must be set.
    /// </summary>
    public static CreatedOffer CreateOffer(
        byte[] makerPkScript,
        byte[] makerPublicKey,
        byte[] emulatorPubkey,
        OutputDescriptor server,
        Network network,
        long wantAmount,
        AssetId? wantAsset = null,
        AssetId? offerAsset = null)
    {
        if (wantAsset is null == (offerAsset is null))
        {
            throw new ArgumentException(
                "set exactly one of wantAsset (BTC→asset) or offerAsset (asset→BTC)");
        }

        var offer = new Offer
        {
            SwapPkScript = [],
            WantAmount = wantAmount,
            WantAsset = wantAsset,
            OfferAsset = offerAsset,
            MakerPkScript = makerPkScript,
            MakerPublicKey = makerPublicKey,
            EmulatorPubkey = emulatorPubkey,
        };

        var contract = BuildContract(offer, server, network);
        var address = contract.GetArkAddress();
        offer.SwapPkScript = address.ScriptPubKey.ToBytes();

        var payload = OfferCodec.Encode(offer);
        return new CreatedOffer(
            OfferHex: Convert.ToHexString(payload).ToLowerInvariant(),
            Payload: payload,
            Address: address.ToString(false),
            SwapPkScript: offer.SwapPkScript,
            Contract: contract);
    }

    /// <summary>
    /// Wrap an x-only key as a taproot output descriptor. Only the x-only value is consumed
    /// downstream (via <c>ToXOnlyPubKey</c>); the even-Y compressed form is a valid carrier.
    /// </summary>
    private static OutputDescriptor XOnlyToDescriptor(byte[] xOnly, Network network)
    {
        var compressed = new byte[33];
        compressed[0] = 0x02;
        Array.Copy(xOnly, 0, compressed, 1, 32);
        return KeyExtensions.ParseOutputDescriptor(Convert.ToHexString(compressed).ToLowerInvariant(), network);
    }
}
