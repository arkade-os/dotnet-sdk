using NArk.Abstractions.Extensions;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Services;
using NArk.Core.Assets;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Tests.Arkade;

[TestFixture]
public class OfferTests
{
    private static readonly OutputDescriptor Server = KeyExtensions.ParseOutputDescriptor(
        "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88", Network.RegTest);

    // ─── TLV codec ────────────────────────────────────────────────────

    [Test]
    public void Tlv_RoundTrips_BtcToAsset()
    {
        var offer = new Offer
        {
            SwapPkScript = TaprootSpk(1),
            WantAmount = 123_456,
            WantAsset = AssetId.Create(Convert.ToHexString(XOnly(9)), 7),
            MakerPkScript = TaprootSpk(2),
            MakerPublicKey = XOnly(3),
            EmulatorPubkey = XOnly(4),
        };

        var decoded = OfferCodec.Decode(OfferCodec.Encode(offer));

        Assert.That(decoded.SwapPkScript, Is.EqualTo(offer.SwapPkScript));
        Assert.That(decoded.WantAmount, Is.EqualTo(123_456));
        Assert.That(decoded.WantAsset!.ToString(), Is.EqualTo(offer.WantAsset!.ToString()));
        Assert.That(decoded.OfferAsset, Is.Null);
        Assert.That(decoded.MakerPkScript, Is.EqualTo(offer.MakerPkScript));
        Assert.That(decoded.MakerPublicKey, Is.EqualTo(offer.MakerPublicKey));
        Assert.That(decoded.EmulatorPubkey, Is.EqualTo(offer.EmulatorPubkey));
    }

    [Test]
    public void Tlv_RoundTrips_AssetToBtc()
    {
        var offer = new Offer
        {
            SwapPkScript = TaprootSpk(1),
            WantAmount = 10_000,
            OfferAsset = AssetId.Create(Convert.ToHexString(XOnly(9)), 2),
            MakerPkScript = TaprootSpk(2),
            MakerPublicKey = XOnly(3),
            EmulatorPubkey = XOnly(4),
        };

        var decoded = OfferCodec.Decode(OfferCodec.Encode(offer));

        Assert.That(decoded.WantAsset, Is.Null);
        Assert.That(decoded.OfferAsset!.ToString(), Is.EqualTo(offer.OfferAsset!.ToString()));
    }

    [Test]
    public void Decode_RejectsTruncatedPayload()
    {
        var bytes = OfferCodec.Encode(SampleBtcToAssetOffer());
        Assert.Throws<FormatException>(() => OfferCodec.Decode(bytes[..^3]));
    }

    [Test]
    public void OfferPacket_WrapsWithType0x03_AndRoundTrips()
    {
        var offer = SampleBtcToAssetOffer();
        var packet = OfferPacket.FromOffer(offer);

        Assert.That(packet.PacketType, Is.EqualTo((byte)0x03));
        Assert.That(packet.SerializePacketData(), Is.EqualTo(OfferCodec.Encode(offer)));
        Assert.That(packet.ToOffer().SwapPkScript, Is.EqualTo(offer.SwapPkScript));
    }

    // ─── CreateOffer + cancel determinism ─────────────────────────────

    [Test]
    public void CreateOffer_ThenDecodeAndRebuild_DerivesSameAddress()
    {
        var makerPkScript = TaprootSpk(2);
        var makerKey = XOnly(3);
        var emulator = XOnly(4);
        var wantAsset = AssetId.Create(Convert.ToHexString(XOnly(9)), 7);

        var created = OfferBuilder.CreateOffer(
            makerPkScript, makerKey, emulator, Server, Network.RegTest, wantAmount: 500, wantAsset: wantAsset);

        Assert.That(created.Address, Does.StartWith("tark1"));
        Assert.That(created.SwapPkScript, Is.EqualTo(created.Contract.GetArkAddress().ScriptPubKey.ToBytes()));

        // Cancel rebuilds the contract from the wire offer with the current server key — the
        // derived address MUST match the funded one.
        var rebuilt = OfferBuilder.BuildContract(OfferCodec.Decode(created.Payload), Server, Network.RegTest);
        Assert.That(rebuilt.GetArkAddress().ToString(false), Is.EqualTo(created.Address));
    }

    [Test]
    public void BuildContract_ExplicitMakerDescriptor_DerivesSameAddress()
    {
        // Cancel rebuilds with the maker's real (wallet-spendable) descriptor rather than the
        // x-only-only wire form; since only the x-only drives the tree, the address must match.
        var makerKey = KeyFor(3);
        var offer = new Offer
        {
            SwapPkScript = [],
            WantAmount = 500,
            WantAsset = AssetId.Create(Convert.ToHexString(XOnly(9)), 1),
            MakerPkScript = TaprootSpk(2),
            MakerPublicKey = makerKey.PubKey.TaprootInternalKey.ToBytes(),
            EmulatorPubkey = XOnly(4),
        };

        var wire = OfferBuilder.BuildContract(offer, Server, Network.RegTest);
        var real = KeyExtensions.ParseOutputDescriptor(makerKey.PubKey.ToHex(), Network.RegTest);
        var cancel = OfferBuilder.BuildContract(offer, Server, Network.RegTest, real);

        Assert.That(cancel.GetArkAddress().ToString(false), Is.EqualTo(wire.GetArkAddress().ToString(false)));
    }

    [Test]
    public void CreateOffer_RequiresExactlyOneAssetSide()
    {
        var maker = TaprootSpk(2);
        var key = XOnly(3);
        var emu = XOnly(4);
        var asset = AssetId.Create(Convert.ToHexString(XOnly(9)), 1);

        // neither
        Assert.Throws<ArgumentException>(() => OfferBuilder.CreateOffer(maker, key, emu, Server, Network.RegTest, 500));
        // both
        Assert.Throws<ArgumentException>(() => OfferBuilder.CreateOffer(
            maker, key, emu, Server, Network.RegTest, 500, wantAsset: asset, offerAsset: asset));
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private static Offer SampleBtcToAssetOffer() => new()
    {
        SwapPkScript = TaprootSpk(1),
        WantAmount = 500,
        WantAsset = AssetId.Create(Convert.ToHexString(XOnly(9)), 1),
        MakerPkScript = TaprootSpk(2),
        MakerPublicKey = XOnly(3),
        EmulatorPubkey = XOnly(4),
    };

    /// <summary>A deterministic key from a seed byte.</summary>
    private static Key KeyFor(byte seed)
    {
        var s = new byte[32];
        s[0] = seed;
        s[31] = 1;
        return new Key(s);
    }

    /// <summary>A deterministic 32-byte x-only key.</summary>
    private static byte[] XOnly(byte seed) => KeyFor(seed).PubKey.TaprootInternalKey.ToBytes();

    /// <summary>A 34-byte taproot scriptPubKey: OP_1 PUSH32 &lt;program&gt;.</summary>
    private static byte[] TaprootSpk(byte seed) => [0x51, 0x20, .. XOnly(seed)];
}
