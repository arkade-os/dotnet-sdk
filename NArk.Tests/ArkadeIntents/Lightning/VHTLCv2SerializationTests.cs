using NArk.Abstractions.Extensions;
using NArk.Arkade.Contracts;
using NArk.Abstractions.Contracts;
using NArk.Core.Contracts;
using NArk.Abstractions.VTXOs;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Tests.ArkadeIntents.Lightning;

/// <summary>
/// The <c>arkcontract=</c> round trip for every covenant option a <see cref="VHTLCv2Contract"/> can
/// carry.
/// </summary>
/// <remarks>
/// Each option changes which leaves the ladder has, or what one of them commits to, and so changes
/// the address. A key dropped on the way out — or read back as "not set" on the way in — therefore
/// rebuilds a DIFFERENT contract from the row that was written, and the wallet then watches an
/// address nothing was ever funded to. That failure names nothing: the parse succeeds, the contract
/// is well-formed, and only the address is wrong. So a half-written row is refused here rather than
/// completed with a default.
/// </remarks>
[TestFixture]
public class VHTLCv2SerializationTests
{
    [TestCase("no-covenant")]
    [TestCase("claim-only")]
    [TestCase("refund-only")]
    [TestCase("both")]
    [TestCase("asset")]
    [TestCase("strict-sats")]
    [TestCase("strict-asset")]
    public void EveryShape_RebuildsTheSameAddress(string shape)
    {
        var contract = Contract(shape);

        var parsed = ArkContractParser.Parse(contract.ToString(), Network.RegTest);

        Assert.That(parsed, Is.InstanceOf<VHTLCv2Contract>());
        Assert.That(
            parsed!.GetArkAddress().ToString(false),
            Is.EqualTo(contract.GetArkAddress().ToString(false)),
            "a rebuilt contract that derives a different address is not the same contract");
    }

    [TestCase("asset")]
    [TestCase("strict-asset")]
    public void EveryShape_RebuildsTheSameOptions(string shape)
    {
        // The address above is the assertion that matters, but it collapses every option into 32
        // bytes. This one says which option came back, so a diff has something to point at.
        var contract = Contract(shape);

        var parsed = (VHTLCv2Contract)ArkContractParser.Parse(contract.ToString(), Network.RegTest)!;

        Assert.Multiple(() =>
        {
            Assert.That(parsed.NonInteractiveClaim?.ReceiverPkScript,
                Is.EqualTo(contract.NonInteractiveClaim?.ReceiverPkScript));
            Assert.That(parsed.NonInteractiveClaim?.Strict, Is.EqualTo(contract.NonInteractiveClaim?.Strict));
            Assert.That(parsed.NonInteractiveRefund?.SenderPkScript,
                Is.EqualTo(contract.NonInteractiveRefund?.SenderPkScript));
            Assert.That(parsed.Asset?.GenesisTxid, Is.EqualTo(contract.Asset?.GenesisTxid));
            Assert.That(parsed.Asset?.GroupIndex, Is.EqualTo(contract.Asset?.GroupIndex));
        });
    }

    [Test]
    public void APreimage_RoundTripsWithoutChangingTheAddress()
    {
        // Not part of the script — every leaf commits to the hash — so the same contract with and
        // without the secret must derive the same address. If it did not, holding the preimage would
        // move the swap, which is the one thing a claim cannot survive.
        var withSecret = Contract("both", Secret);
        var withoutSecret = Contract("both");

        var parsed = (VHTLCv2Contract)ArkContractParser.Parse(withSecret.ToString(), Network.RegTest)!;

        Assert.Multiple(() =>
        {
            Assert.That(parsed.Preimage, Is.EqualTo(Secret));
            Assert.That(
                withSecret.GetArkAddress().ToString(false),
                Is.EqualTo(withoutSecret.GetArkAddress().ToString(false)));
        });
    }

    [Test]
    public void APreimageThatOpensADifferentContract_IsRefused()
    {
        // A wrong secret carried by a contract that looks complete. Caught here rather than at claim
        // time, where the first sign of it is a spend already broadcast and rejected.
        var row = Row("both");
        row["preimage"] = Convert.ToHexString(Enumerable.Repeat((byte)0x5a, 32).ToArray());

        Assert.Throws<ArgumentException>(() => VHTLCv2Contract.Parse(row, Network.RegTest));
    }

    [Test]
    public void AContractWithoutTheSecret_RefusesToClaimOnItsOwn()
    {
        // Rebuilt from a row that never carried one: the same contract, seen by someone who cannot
        // claim it. That is a refusal, not a null preimage pushed into a witness.
        Assert.Throws<InvalidOperationException>(
            () => Contract("both").ToClaimCoin("wallet", Vtxo()));
    }

    [TestCase("niClaimEmulator", TestName = "A claim leaf missing its emulator key")]
    [TestCase("niClaimPkScript", TestName = "A claim leaf missing its destination")]
    [TestCase("niRefundEmulator", TestName = "A refund leaf missing its emulator key")]
    [TestCase("niRefundPkScript", TestName = "A refund leaf missing its destination")]
    [TestCase("assetTxid", TestName = "An asset missing its genesis txid")]
    [TestCase("assetGroupIndex", TestName = "An asset missing its group index")]
    public void AHalfWrittenPair_IsRefused(string dropped)
    {
        var row = Row("strict-asset");
        row.Remove(dropped);

        Assert.Throws<FormatException>(() => VHTLCv2Contract.Parse(row, Network.RegTest));
    }

    [Test]
    public void AStrictAssetBoundWithoutItsSatBound_IsRefused()
    {
        // Read as "not strict" this would rebuild the DEFAULT claim covenant, which is weaker than
        // the row asked for.
        var row = Row("strict-asset");
        row.Remove("strictClaimAmount");

        Assert.Throws<FormatException>(() => VHTLCv2Contract.Parse(row, Network.RegTest));
    }

    [Test]
    public void AStrictBoundWithoutTheLeafItBounds_IsRefused()
    {
        var row = Row("strict-sats");
        row.Remove("niClaimPkScript");
        row.Remove("niClaimEmulator");

        Assert.Throws<FormatException>(() => VHTLCv2Contract.Parse(row, Network.RegTest));
    }

    private static Dictionary<string, string> Row(string shape) =>
        IArkContractParser.GetContractData(Contract(shape).ToString());

    /// <summary>The secret behind the hash every shape below commits to.</summary>
    private static readonly byte[] Secret = Enumerable.Repeat((byte)0x42, 32).ToArray();

    private static ArkVtxo Vtxo() =>
        new(Script: "5120" + new string('a', 64), TransactionId: new string('b', 64),
            TransactionOutputIndex: 0, Amount: 50_000,
            SpentByTransactionId: null, SettledByTransactionId: null, Swept: false,
            CreatedAt: DateTimeOffset.UtcNow, ExpiresAt: null, ExpiresAtHeight: null, ArkTxid: null);

    private static VHTLCv2Contract Contract(string shape, byte[]? preimage = null)
    {
        var emulator = XOnly(0x05);
        var claimPkScript = P2tr(0x06);
        var refundPkScript = P2tr(0x07);
        var asset = new VHTLCv2Asset(Enumerable.Repeat((byte)0x33, 32).ToArray(), 3);

        VHTLCv2NonInteractiveClaim Claim(VHTLCv2StrictClaim? strict = null) =>
            new(claimPkScript, emulator, strict);
        VHTLCv2NonInteractiveRefund Refund() => new(refundPkScript, emulator);

        var (claim, refund, denomination) = shape switch
        {
            "no-covenant" => (null, null, (VHTLCv2Asset?)null),
            "claim-only" => (Claim(), null, null),
            "refund-only" => ((VHTLCv2NonInteractiveClaim?)null, Refund(), null),
            "both" => (Claim(), Refund(), null),
            "asset" => (Claim(), Refund(), asset),
            "strict-sats" => (Claim(new VHTLCv2StrictClaim(25_000)), null, null),
            "strict-asset" => (Claim(new VHTLCv2StrictClaim(25_000, 1_234_567)), Refund(), asset),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "unknown shape"),
        };

        return new VHTLCv2Contract(
            Descriptor(0x02), Descriptor(0x03), Descriptor(0x04),
            new uint160(
                NBitcoin.Crypto.Hashes.RIPEMD160(NBitcoin.Crypto.Hashes.SHA256(Secret), 32), false),
            new LockTime(1_800_000_000),
            new Sequence(TimeSpan.FromSeconds(4096)),
            new Sequence(TimeSpan.FromSeconds(4096)),
            new Sequence(TimeSpan.FromSeconds(8192)),
            claim, refund, denomination, preimage);
    }

    private static OutputDescriptor Descriptor(byte fill) =>
        KeyExtensions.ParseOutputDescriptor(
            "02" + Convert.ToHexString(KeyBytes(fill)).ToLowerInvariant(), Network.RegTest);

    private static ECXOnlyPubKey XOnly(byte fill) => ECXOnlyPubKey.Create(KeyBytes(fill));

    private static byte[] P2tr(byte fill) => [0x51, 0x20, .. KeyBytes(fill)];

    /// <summary>A valid x-only key: fill a scalar and take the public key it derives.</summary>
    private static byte[] KeyBytes(byte fill) =>
        new Key(Enumerable.Repeat(fill, 32).ToArray()).PubKey.Compress().ToBytes()[1..];
}
