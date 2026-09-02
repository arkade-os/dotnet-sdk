using System.Text.Json;
using NArk.Abstractions.Extensions;
using NArk.Arkade.Contracts;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Tests.Arkade;

/// <summary>
/// Pins the covenant suite's ninth leaf — <c>nonInteractiveRefundWithoutReceiver</c> — to the
/// TypeScript SDK's own derivation, and pins that leaving it off keeps the first eight leaves, and
/// so every address already derived without it, untouched.
/// </summary>
/// <remarks>
/// <c>Fixtures/vhtlc-v2-nine-leaf.json</c> is shared verbatim with the ts-sdk repo's own
/// <c>test/script/vhtlc-vectors.test.ts</c> fixture: if this class and that fixture disagree, this
/// class is what is wrong. See the fixture's own <c>comment</c> field for exactly how the vectors
/// were generated.
/// </remarks>
[TestFixture]
public class VHTLCv2NineLeafTests
{
    private static readonly NineLeafFixture Fixture = LoadFixture();

    [Test]
    public void TheNineLeafLadder_MatchesTheSdkVectors()
    {
        var contract = NineLeaf();

        var leaves = contract.GetTapScriptList().Select(LeafHex).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(leaves, Has.Length.EqualTo(9));
            Assert.That(leaves[8], Is.EqualTo(Fixture.Leaves["nonInteractiveRefundWithoutReceiver"]));
            // The address is the agreement: nothing is exchanged to confirm it, so a divergence
            // anywhere above surfaces here as funds at an address the counterparty never quoted.
            Assert.That(Hex(contract.GetScriptPubKey().ToBytes()), Is.EqualTo(Fixture.PkScript));
        });
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
        var contract = NineLeaf();
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

    [TestCase("nonInteractiveClaim")]
    [TestCase("nonInteractiveRefund")]
    public void EveryCovenant_MatchesTheSdkVector(string leaf)
    {
        // The bytes the emulator actually runs before it will co-sign. The leaf hexes above pin the
        // TWEAKED key each leaf commits to; these pin the script that key was tweaked by, so a
        // divergence points at the covenant rather than at "some key is different".
        var contract = NineLeaf();
        var built = leaf switch
        {
            "nonInteractiveClaim" => contract.NonInteractiveClaimArkadeScript,
            "nonInteractiveRefund" => contract.NonInteractiveRefundArkadeScript,
            _ => throw new ArgumentOutOfRangeException(nameof(leaf), leaf, "unknown covenant"),
        };

        Assert.That(Hex(built), Is.EqualTo(Fixture.ArkadeScripts[leaf]));
    }

    [Test]
    public void BothRefundLeaves_ShareOneCovenantKey()
    {
        // They pin the same destination, so they must commit to the same tweaked key. Deriving it
        // separately would leave that a coincidence; the contract derives it once and reads it twice,
        // which is what these two vectors agreeing on the trailing 32 bytes proves.
        var contract = NineLeaf();

        var refund = Fixture.Leaves["nonInteractiveRefund"];
        var withoutReceiver = Fixture.Leaves["nonInteractiveRefundWithoutReceiver"];

        Assert.Multiple(() =>
        {
            // Every leaf ends push(32-byte key) CHECKSIG: the last 66 hex chars are "20" + key + "ac".
            Assert.That(withoutReceiver[^66..], Is.EqualTo(refund[^66..]));
            Assert.That(
                Hex(contract.CreateNonInteractiveRefundWithoutReceiverScript().Build().Script.ToBytes()),
                Does.EndWith(refund[^66..]));
        });
    }

    [Test]
    public void WithoutTheFlag_TheFirstEightLeavesAreUntouched()
    {
        var eight = EightLeaf();
        var nine = NineLeaf();

        Assert.Multiple(() =>
        {
            Assert.That(eight.GetTapScriptList(), Has.Length.EqualTo(8));
            // A ninth leaf is a different merkle root and so a different contract, not a variant of
            // the same one.
            Assert.That(
                eight.GetArkAddress().ToString(false),
                Is.Not.EqualTo(nine.GetArkAddress().ToString(false)));
            // Byte-identical, leaf by leaf — not merely equal in number.
            Assert.That(
                eight.GetTapScriptList().Select(LeafHex),
                Is.EqualTo(nine.GetTapScriptList().Take(8).Select(LeafHex)));
        });
    }

    [Test]
    public void WithoutTheFlag_TheLeafBuilderRefusesRatherThanReturningAnUnrootedLeaf()
    {
        // This leaf is not in the eight-leaf merkle root, so a witness built from it would be
        // rejected on-chain with nothing at the SDK level saying why. The guard turns a silent
        // on-chain rejection into an immediate, named failure.
        var ex = Assert.Throws<InvalidOperationException>(
            () => EightLeaf().CreateNonInteractiveRefundWithoutReceiverScript());

        Assert.That(ex!.Message, Does.Contain("non-interactive refund-without-receiver"));
    }

    [Test]
    public void WithoutTheSuite_TheLeafBuilderRefusesToo()
    {
        // The covenant group is absent entirely: a plain six-leaf VHTLC has no emulator key to tweak,
        // so there is no covenant destination this leaf could even be pinned to.
        var o = Fixture.Options;
        var plain = new VHTLCv2Contract(
            Descriptor(o.Server), Descriptor(o.Sender), Descriptor(o.Receiver),
            PreimageHash(o), new LockTime(uint.Parse(o.RefundLocktime)),
            ToSequence(o.UnilateralClaimDelay),
            ToSequence(o.UnilateralRefundDelay),
            ToSequence(o.UnilateralRefundWithoutReceiverDelay));

        Assert.Multiple(() =>
        {
            Assert.That(plain.GetTapScriptList(), Has.Length.EqualTo(6));
            Assert.Throws<InvalidOperationException>(
                () => plain.CreateNonInteractiveRefundWithoutReceiverScript());
        });
    }

    /// <summary>
    /// The nine-leaf contract, with the ninth leaf driven by the fixture's own
    /// <c>withoutReceiver</c> field rather than by a literal here — so a fixture swapped for a
    /// differently-shaped one fails these tests instead of being quietly overridden.
    /// </summary>
    private static VHTLCv2Contract NineLeaf() =>
        MakeContract(Fixture.Options.NonInteractiveRefund.WithoutReceiver);

    /// <summary>The same contract without the ninth leaf: the shape every lockup so far carries.</summary>
    private static VHTLCv2Contract EightLeaf() => MakeContract(withoutReceiver: false);

    private static VHTLCv2Contract MakeContract(bool withoutReceiver)
    {
        var o = Fixture.Options;
        return new VHTLCv2Contract(
            server: Descriptor(o.Server),
            sender: Descriptor(o.Sender),
            receiver: Descriptor(o.Receiver),
            hash: PreimageHash(o),
            refundLocktime: new LockTime(uint.Parse(o.RefundLocktime)),
            unilateralClaimDelay: ToSequence(o.UnilateralClaimDelay),
            unilateralRefundDelay: ToSequence(o.UnilateralRefundDelay),
            unilateralRefundWithoutReceiverDelay: ToSequence(o.UnilateralRefundWithoutReceiverDelay),
            nonInteractiveClaim: new VHTLCv2NonInteractiveClaim(
                Convert.FromHexString(o.NonInteractiveClaim.ReceiverPkScript),
                XOnly(o.NonInteractiveClaim.EmulatorPubkey)),
            nonInteractiveRefund: new VHTLCv2NonInteractiveRefund(
                Convert.FromHexString(o.NonInteractiveRefund.SenderPkScript),
                XOnly(o.NonInteractiveRefund.EmulatorPubkey),
                withoutReceiver));
    }

    private static uint160 PreimageHash(FixtureOptions o) =>
        new(Convert.FromHexString(o.PreimageHash), false);

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
        var path = Path.Combine(
            TestContext.CurrentContext.TestDirectory, "Arkade", "Fixtures", "vhtlc-v2-nine-leaf.json");
        return JsonSerializer.Deserialize<NineLeafFixture>(
                   File.ReadAllText(path),
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException($"Failed to load fixture {path}");
    }

    /// <summary>The fixture's top level.</summary>
    public sealed record NineLeafFixture(
        FixtureOptions Options,
        string PkScript,
        Dictionary<string, string> Leaves,
        Dictionary<string, string> ArkadeScripts);

    /// <summary>The <c>VHTLC.Options</c> the fixture was generated from.</summary>
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

    /// <summary>A relative delay, as the fixture writes it.</summary>
    public sealed record FixtureDelay(string Type, string Value);

    /// <summary>The fixture's non-interactive claim options.</summary>
    public sealed record FixtureCovenantClaim(string ReceiverPkScript, string EmulatorPubkey);

    /// <summary>The fixture's non-interactive refund options, including the ninth-leaf flag.</summary>
    public sealed record FixtureCovenantRefund(
        string SenderPkScript, string EmulatorPubkey, bool WithoutReceiver);
}
