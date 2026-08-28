using System.Text.Json;
using NArk.Abstractions.Extensions;
using NArk.Arkade.Contracts;
using NArk.Core.Contracts;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Tests.Arkade;

/// <summary>
/// Pins the ninth, off-by-default leaf — <c>nonInteractiveRefundWithoutReceiver</c> — to the
/// TypeScript SDK's own derivation, and pins that turning the flag on leaves the first eight
/// leaves, and so every address derived without it, untouched.
/// </summary>
/// <remarks>
/// <c>vhtlc-v2-nine-leaf.json</c> is shared verbatim with the ts-sdk repo's own
/// <c>test/script/vhtlc-vectors.test.ts</c> fixture: if this class and that fixture disagree,
/// this class is what is wrong. See the fixture's own <c>comment</c> field for exactly how the
/// vectors were generated.
/// </remarks>
[TestFixture]
public class VHTLCv2NineLeafTests
{
    private static readonly NineLeafFixture Fixture = LoadFixture();

    [Test]
    public void NineLeafScript_MatchesSdkVectors()
    {
        var contract = MakeContract(nonInteractiveRefundWithoutReceiver: true);

        var leaves = contract.GetTapScriptList().Select(LeafHex).ToArray();

        Assert.That(leaves, Has.Length.EqualTo(9));
        Assert.That(leaves[8], Is.EqualTo(Fixture.Leaves["nonInteractiveRefundWithoutReceiver"]));
        Assert.That(Hex(contract.GetScriptPubKey().ToBytes()), Is.EqualTo(Fixture.PkScript));
    }

    [Test]
    public void WithoutTheFlag_TheAddressIsUnchanged()
    {
        var eight = MakeContract(nonInteractiveRefundWithoutReceiver: false);
        var nine = MakeContract(nonInteractiveRefundWithoutReceiver: true);

        Assert.That(eight.GetTapScriptList(), Has.Length.EqualTo(8));
        Assert.That(
            eight.GetArkAddress().ToString(false),
            Is.Not.EqualTo(nine.GetArkAddress().ToString(false)));

        // The eight are byte-identical, not merely equal in number.
        Assert.That(
            eight.GetTapScriptList().Select(LeafHex),
            Is.EqualTo(nine.GetTapScriptList().Take(8).Select(LeafHex)));
    }

    [TestCase("claim")]
    [TestCase("refund")]
    [TestCase("refundWithoutReceiver")]
    [TestCase("unilateralClaim")]
    [TestCase("unilateralRefund")]
    [TestCase("unilateralRefundWithoutReceiver")]
    [TestCase("nonInteractiveClaim")]
    [TestCase("nonInteractiveRefund")]
    [TestCase("nonInteractiveRefundWithoutReceiver")]
    public void EveryLeaf_MatchesTheSdkVector(string leaf)
    {
        var contract = MakeContract(nonInteractiveRefundWithoutReceiver: true);
        var built = leaf switch
        {
            "claim" => contract.CreateClaimScript(),
            "refund" => contract.CreateRefundScript(),
            "refundWithoutReceiver" => contract.CreateRefundWithoutReceiverScript(),
            "unilateralClaim" => contract.CreateUnilateralClaimScript(),
            "unilateralRefund" => contract.CreateUnilateralRefundScript(),
            "unilateralRefundWithoutReceiver" => contract.CreateUnilateralRefundWithoutReceiverScript(),
            "nonInteractiveClaim" => contract.CreateNonInteractiveClaimScript(),
            "nonInteractiveRefund" => contract.CreateNonInteractiveRefundScript(),
            "nonInteractiveRefundWithoutReceiver" => contract.CreateNonInteractiveRefundWithoutReceiverScript(),
            _ => throw new ArgumentOutOfRangeException(nameof(leaf), leaf, "unknown leaf"),
        };

        Assert.That(Hex(built.Build().Script.ToBytes()), Is.EqualTo(Fixture.Leaves[leaf]));
    }

    [Test]
    public void RoundTrip_SerializesTheFlagAsTheStringOneAndOmitsItWhenUnset()
    {
        var nine = MakeContract(nonInteractiveRefundWithoutReceiver: true);
        var eight = MakeContract(nonInteractiveRefundWithoutReceiver: false);

        // Exact wire shape for this one flag: the key name and the value "1" (not "true") are
        // what let the FLAG ITSELF round-trip identically between the two SDKs. That is narrower
        // than "a contract round-trips across SDKs" in general — it does not, for the many other
        // keys in GetContractData() — see that method's own comment for the specifics.
        Assert.That(nine.ToString(), Does.Contain("&nonInteractiveRefundWithoutReceiver=1"));
        Assert.That(eight.ToString(), Does.Not.Contain("nonInteractiveRefundWithoutReceiver"));

        var parsedNine = ArkContractParser.Parse(nine.ToString(), Network.RegTest) as VHTLCv2Contract;
        var parsedEight = ArkContractParser.Parse(eight.ToString(), Network.RegTest) as VHTLCv2Contract;

        Assert.That(parsedNine, Is.Not.Null);
        Assert.That(parsedEight, Is.Not.Null);
        Assert.That(parsedNine!.NonInteractiveRefundWithoutReceiver, Is.True);
        Assert.That(parsedEight!.NonInteractiveRefundWithoutReceiver, Is.False);
        Assert.That(
            parsedNine.GetArkAddress().ToString(false), Is.EqualTo(nine.GetArkAddress().ToString(false)));
        Assert.That(
            parsedEight.GetArkAddress().ToString(false), Is.EqualTo(eight.GetArkAddress().ToString(false)));
    }

    [Test]
    public void RoundTrip_ThrowsOnAMalformedFlagValue()
    {
        // Reading a present-but-wrong value as "not set" would silently re-derive the eight-leaf
        // script — a different address — with no indication the row was corrupt. ts-sdk's
        // deserializeParams throws for the same reason; the two SDKs must fail identically here
        // rather than quietly disagreeing about what a contract's address is.
        var nine = MakeContract(nonInteractiveRefundWithoutReceiver: true);
        var corrupted = nine.ToString().Replace(
            "nonInteractiveRefundWithoutReceiver=1", "nonInteractiveRefundWithoutReceiver=true");

        var ex = Assert.Throws<ArgumentException>(() => ArkContractParser.Parse(corrupted, Network.RegTest));

        Assert.That(ex!.Message, Does.Contain("nonInteractiveRefundWithoutReceiver"));
        Assert.That(ex.Message, Does.Contain("\"true\""));
    }

    [Test]
    public void RoundTrip_AbsentFlagIsFalseNotAThrow()
    {
        // The negative space of the check above: an eight-leaf contract has no key at all, and
        // that must keep parsing cleanly rather than being caught by the malformed-value guard.
        var eight = MakeContract(nonInteractiveRefundWithoutReceiver: false);

        var parsed = ArkContractParser.Parse(eight.ToString(), Network.RegTest) as VHTLCv2Contract;

        Assert.That(parsed, Is.Not.Null);
        Assert.That(parsed!.NonInteractiveRefundWithoutReceiver, Is.False);
    }

    [Test]
    public void CreateNonInteractiveRefundWithoutReceiverScript_ThrowsWhenTheFlagIsOff()
    {
        // Called directly on an eight-leaf contract this leaf is not in the taproot tree, so a
        // witness built against it would be silently rejected on-chain with no SDK-level error —
        // the guard turns that into an immediate, named failure instead. ts-sdk's equivalent
        // accessor throws the identical message for the identical reason.
        var eight = MakeContract(nonInteractiveRefundWithoutReceiver: false);

        var ex = Assert.Throws<InvalidOperationException>(
            () => eight.CreateNonInteractiveRefundWithoutReceiverScript());

        Assert.That(ex!.Message, Is.EqualTo("VHTLC has no non-interactive refund-without-receiver leaf"));
    }

    [Test]
    public void Fixture_IsTheNineLeafVariant()
    {
        // A cheap guard that this fixture has not been swapped for a differently-shaped one:
        // WithoutReceiver is otherwise deserialized and never read anywhere in this file.
        Assert.That(Fixture.Options.NonInteractiveRefund.WithoutReceiver, Is.True);
    }

    private static VHTLCv2Contract MakeContract(bool nonInteractiveRefundWithoutReceiver)
    {
        var o = Fixture.Options;
        return new VHTLCv2Contract(
            server: Descriptor(o.Server),
            sender: Descriptor(o.Sender),
            receiver: Descriptor(o.Receiver),
            hash: new uint160(Convert.FromHexString(o.PreimageHash), false),
            refundLocktime: new LockTime(uint.Parse(o.RefundLocktime)),
            unilateralClaimDelay: ToSequence(o.UnilateralClaimDelay),
            unilateralRefundDelay: ToSequence(o.UnilateralRefundDelay),
            unilateralRefundWithoutReceiverDelay: ToSequence(o.UnilateralRefundWithoutReceiverDelay),
            // Both covenant leaves tweak the same emulator key; the fixture happens to carry it
            // twice (once per leaf's options block) because that is how VHTLC.Options is shaped.
            emulatorPubKey: XOnly(o.NonInteractiveRefund.EmulatorPubkey),
            nonInteractiveClaimPkScript: Convert.FromHexString(o.NonInteractiveClaim.ReceiverPkScript),
            nonInteractiveRefundPkScript: Convert.FromHexString(o.NonInteractiveRefund.SenderPkScript),
            nonInteractiveRefundWithoutReceiver: nonInteractiveRefundWithoutReceiver);
    }

    private static Sequence ToSequence(FixtureDelay delay) => delay.Type switch
    {
        "blocks" => new Sequence(uint.Parse(delay.Value)),
        "seconds" => new Sequence(TimeSpan.FromSeconds(uint.Parse(delay.Value))),
        _ => throw new FormatException($"unknown delay type '{delay.Type}'"),
    };

    private static ECXOnlyPubKey XOnly(string hex) => ECXOnlyPubKey.Create(Convert.FromHexString(hex));

    /// <summary>Wrap a fixture's compressed-pubkey hex as a taproot output descriptor.</summary>
    private static OutputDescriptor Descriptor(string compressedPubKeyHex) =>
        KeyExtensions.ParseOutputDescriptor(compressedPubKeyHex, Network.RegTest);

    private static string LeafHex(TapScript leaf) => Hex(leaf.Script.ToBytes());

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static NineLeafFixture LoadFixture()
    {
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Arkade", "vhtlc-v2-nine-leaf.json");
        return JsonSerializer.Deserialize<NineLeafFixture>(
                   File.ReadAllText(path),
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException($"Failed to load fixture {path}");
    }

    public sealed record NineLeafFixture(
        FixtureOptions Options,
        string PkScript,
        Dictionary<string, string> Leaves);

    public sealed record FixtureOptions(
        string Sender,
        string Receiver,
        string Server,
        string PreimageHash,
        string RefundLocktime,
        FixtureDelay UnilateralClaimDelay,
        FixtureDelay UnilateralRefundDelay,
        FixtureDelay UnilateralRefundWithoutReceiverDelay,
        FixtureCovenantClaim NonInteractiveClaim,
        FixtureCovenantRefund NonInteractiveRefund);

    public sealed record FixtureDelay(string Type, string Value);

    public sealed record FixtureCovenantClaim(string ReceiverPkScript, string EmulatorPubkey);

    public sealed record FixtureCovenantRefund(string SenderPkScript, string EmulatorPubkey, bool WithoutReceiver);
}
