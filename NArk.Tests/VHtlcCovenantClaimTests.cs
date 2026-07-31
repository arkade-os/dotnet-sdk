using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Core.Contracts;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Tests;

/// <summary>
/// Tests for the optional covenant-claim leaf on <see cref="VHTLCContract"/>.
/// </summary>
/// <remarks>
/// Two properties matter here and they pull in opposite directions: the leaf must
/// match the covenant signer's own construction byte for byte, and its existence
/// must be invisible to every VHTLC that doesn't use it. The first is pinned
/// against a vector generated from arkd's <c>ConditionMultisigClosure</c>; the
/// second is covered by asserting the leaf set, the serialized form, and the parse
/// path all stay unchanged when no key is set.
/// </remarks>
[TestFixture]
public class VHtlcCovenantClaimTests
{
    // Server key from the Go vector, so the generated leaf is directly comparable.
    private static readonly OutputDescriptor Server =
        KeyExtensions.ParseOutputDescriptor(
            "02c6047f9441ed7d6d3045406e95c07cd85c778e4b8cef3ca7abac09b95c709ee5", Network.RegTest);

    private static readonly OutputDescriptor Sender =
        KeyExtensions.ParseOutputDescriptor(
            "030192e796452d6df9697c280542e1560557bcf79a347d925895043136225c7cb4", Network.RegTest);

    private static readonly OutputDescriptor Receiver =
        KeyExtensions.ParseOutputDescriptor(
            "021e1bb85455fe3f5aed60d101aa4dbdb9e7714f6226769a97a17a5331dadcd53b", Network.RegTest);

    private static readonly uint160 Hash =
        new(Convert.FromHexString("f1e2d3c4b5a6978869504132231405f6e7d8c9ba"), false);

    /// <summary>Tweaked covenant co-signer from the Go vector.</summary>
    private static readonly TaprootPubKey CovenantKey =
        new(Convert.FromHexString("77a2e768588b5ced39c389e2ce803041bf9a70d503b34b49edf5970d912dcbb1"));

    /// <summary>
    /// <c>OP_HASH160 &lt;hash&gt; OP_EQUAL OP_VERIFY &lt;server&gt; OP_CHECKSIGVERIFY
    /// &lt;covenant&gt; OP_CHECKSIG</c>, as produced by arkd's ConditionMultisigClosure.
    /// </summary>
    private const string ExpectedLeafHex =
        "a914f1e2d3c4b5a6978869504132231405f6e7d8c9ba87" +
        "6920c6047f9441ed7d6d3045406e95c07cd85c778e4b8cef3ca7abac09b95c709ee5ad" +
        "2077a2e768588b5ced39c389e2ce803041bf9a70d503b34b49edf5970d912dcbb1ac";

    private static VHTLCContract Plain() =>
        new(Server, Sender, Receiver, Hash, new LockTime(265),
            new Sequence(144), new Sequence(144), new Sequence(144));

    private static VHTLCContract WithCovenant() =>
        new(Server, Sender, Receiver, Hash, new LockTime(265),
            new Sequence(144), new Sequence(144), new Sequence(144), CovenantKey);

    [Test]
    public void CovenantClaimScript_MatchesConditionMultisigClosure()
    {
        var leaf = WithCovenant().CreateCovenantClaimScript().Build().Script;

        Assert.That(Convert.ToHexString(leaf.ToBytes()).ToLowerInvariant(), Is.EqualTo(ExpectedLeafHex));
    }

    [Test]
    public void CovenantClaimScript_ThrowsWhenNoKeySet()
    {
        Assert.Throws<InvalidOperationException>(() => Plain().CreateCovenantClaimScript());
    }

    [Test]
    public void WithoutCovenantKey_KeepsSixLeaves()
    {
        Assert.That(Plain().GetTapScriptList(), Has.Length.EqualTo(6));
    }

    [Test]
    public void WithCovenantKey_AppendsSeventhLeaf()
    {
        var plain = Plain().GetTapScriptList();
        var covenant = WithCovenant().GetTapScriptList();

        Assert.Multiple(() =>
        {
            Assert.That(covenant, Has.Length.EqualTo(7));
            // The existing six must keep their identity and order, or every other
            // spending path's control block changes.
            Assert.That(covenant.Take(6).Select(l => l.Script.ToHex()),
                Is.EqualTo(plain.Select(l => l.Script.ToHex())));
            Assert.That(covenant[6].Script.ToHex(), Is.EqualTo(ExpectedLeafHex));
        });
    }

    /// <summary>
    /// The extra leaf changes the taproot output key, so both sides of a swap have to
    /// agree on it before the lockup address is derived.
    /// </summary>
    [Test]
    public void WithCovenantKey_ChangesAddress()
    {
        Assert.That(WithCovenant().GetArkAddress().ToString(false),
            Is.Not.EqualTo(Plain().GetArkAddress().ToString(false)));
    }

    [Test]
    public void WithoutCovenantKey_SerializesWithoutTheField()
    {
        Assert.That(Plain().ToString(), Does.Not.Contain("covenantClaimKey"));
    }

    [Test]
    public void RoundTrip_PreservesCovenantKeyAndAddress()
    {
        var original = WithCovenant();

        var parsed = (VHTLCContract)ArkContractParser.Parse(original.ToString(), Network.RegTest)!;

        Assert.Multiple(() =>
        {
            Assert.That(parsed.CovenantClaimKey, Is.EqualTo(CovenantKey));
            Assert.That(parsed.GetArkAddress().ToString(false),
                Is.EqualTo(original.GetArkAddress().ToString(false)));
        });
    }

    /// <summary>
    /// Contracts written before this field existed must keep parsing to exactly the
    /// contract their bytes described — same leaf set, same address, no covenant path.
    /// </summary>
    [Test]
    public void RoundTrip_ContractDataWithoutTheField_ParsesAsPlainVhtlc()
    {
        var original = Plain();

        var parsed = (VHTLCContract)ArkContractParser.Parse(original.ToString(), Network.RegTest)!;

        Assert.Multiple(() =>
        {
            Assert.That(parsed.CovenantClaimKey, Is.Null);
            Assert.That(parsed.GetTapScriptList(), Has.Length.EqualTo(6));
            Assert.That(parsed.GetArkAddress().ToString(false),
                Is.EqualTo(original.GetArkAddress().ToString(false)));
        });
    }

    [Test]
    public void Create_WithoutCovenantKey_MatchesConstructor()
    {
        var viaFactory = VHTLCContract.Create(Server, Sender, Receiver,
            Hash.ToBytes(false), new LockTime(265),
            VHtlcDelay.Blocks(144), VHtlcDelay.Blocks(144), VHtlcDelay.Blocks(144));

        Assert.That(viaFactory.GetArkAddress().ToString(false),
            Is.EqualTo(Plain().GetArkAddress().ToString(false)));
    }

    [Test]
    public void Create_WithCovenantKey_MatchesConstructor()
    {
        var viaFactory = VHTLCContract.Create(Server, Sender, Receiver,
            Hash.ToBytes(false), new LockTime(265),
            VHtlcDelay.Blocks(144), VHtlcDelay.Blocks(144), VHtlcDelay.Blocks(144), CovenantKey);

        Assert.That(viaFactory.GetArkAddress().ToString(false),
            Is.EqualTo(WithCovenant().GetArkAddress().ToString(false)));
    }
}
