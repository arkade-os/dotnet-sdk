using NArk.Arkade.Covclaim;
using NArk.Arkade.Crypto;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Tests.Arkade;

/// <summary>
/// Byte-compatibility tests for the covclaimd covenant-claim primitives.
/// </summary>
/// <remarks>
/// <para>
/// Covers the ingredients of a covenant claim leaf: the ArkadeScript, its tagged hash,
/// and the tweaked signer key. Every expected value was produced by running covclaimd's
/// own <c>preimage.EnforcePayTo</c> and <c>arkade.ArkadeScriptHash</c> over the same
/// inputs. The assembled leaf is pinned separately, in
/// <c>VHtlcCovenantClaimTests</c>.
/// </para>
/// <para>
/// Pinning these matters more than usual: the ArkadeScript bytes are tagged-hashed into
/// the signer's key, so a single byte of drift produces a leaf that signer will never
/// co-sign — and the failure is silent, the claim simply never happens and the swap
/// quietly times out.
/// </para>
/// <para>
/// Regenerate with the Go snippet documented in the covclaimd client article if
/// the upstream script shape ever changes.
/// </para>
/// </remarks>
[TestFixture]
public class CovenantClaimScriptTests
{
    // secp256k1 generator (x-only), used as the claim destination's witness program.
    private const string ReceiverXOnlyHex =
        "79be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";

    private const string ReceiverPkScriptHex =
        "512079be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";


    private const string EmulatorPubKeyHex =
        "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";


    /// <summary>
    /// OP_PUSHCURRENTINPUTINDEX OP_DUP OP_INSPECTOUTPUTSCRIPTPUBKEY OP_1 OP_EQUALVERIFY
    /// &lt;program&gt; OP_EQUALVERIFY OP_INSPECTOUTPUTVALUE OP_PUSHCURRENTINPUTINDEX
    /// OP_INSPECTINPUTVALUE OP_GREATERTHANOREQUAL
    /// </summary>
    private const string ExpectedArkadeScriptHex =
        "cd76d151882079be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f8179888cfcdc9a2";

    private const string ExpectedArkadeScriptHashHex =
        "4ce27f6833ad56413f84be74636a193640ae1b6c77a4d6baebe102865e1d7a6b";

    private const string ExpectedEmulatorTweakedXOnlyHex =
        "77a2e768588b5ced39c389e2ce803041bf9a70d503b34b49edf5970d912dcbb1";

    private static Script ReceiverPkScript => new(Convert.FromHexString(ReceiverPkScriptHex));

    private static TaprootPubKey EmulatorTaprootPubKey =>
        new(ParsePubKey(EmulatorPubKeyHex).ToXOnlyPubKey().ToBytes());

    private static ECPubKey ParsePubKey(string hex)
    {
        Assert.That(
            ECPubKey.TryCreate(Convert.FromHexString(hex), Context.Instance, out _, out var key),
            Is.True, "test vector pubkey should parse");
        return key;
    }

    [Test]
    public void EnforcePayTo_MatchesGoImplementation()
    {
        var script = CovenantClaimScript.EnforcePayTo(ReceiverPkScript);

        Assert.That(Convert.ToHexString(script).ToLowerInvariant(), Is.EqualTo(ExpectedArkadeScriptHex));
    }

    /// <summary>
    /// The receiver's witness program must appear verbatim in the script — this is
    /// what actually pins the claim destination.
    /// </summary>
    [Test]
    public void EnforcePayTo_EmbedsReceiverWitnessProgram()
    {
        var script = Convert.ToHexString(CovenantClaimScript.EnforcePayTo(ReceiverPkScript))
            .ToLowerInvariant();

        Assert.That(script, Does.Contain(ReceiverXOnlyHex));
    }

    [Test]
    public void EnforcePayTo_DifferentReceivers_ProduceDifferentScripts()
    {
        var other = new Script(Convert.FromHexString(
            "5120" + "c6047f9441ed7d6d3045406e95c07cd85c778e4b8cef3ca7abac09b95c709ee5"));

        Assert.That(
            CovenantClaimScript.EnforcePayTo(other),
            Is.Not.EqualTo(CovenantClaimScript.EnforcePayTo(ReceiverPkScript)));
    }

    [TestCase("0014c6047f9441ed7d6d3045406e95c07cd85c778e4b", Description = "P2WPKH, not P2TR")]
    [TestCase("5121c6047f9441ed7d6d3045406e95c07cd85c778e4b8cef3ca7abac09b95c709ee501",
        Description = "wrong length")]
    [TestCase("", Description = "empty")]
    // Right length, wrong witness version — the only case that reaches the prefix check
    // rather than being rejected on length first.
    [TestCase("522079be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798",
        Description = "34 bytes but OP_2, not OP_1")]
    // Right length and version, but a non-minimal push opcode where OP_DATA_32 belongs.
    [TestCase("514c2079be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f817",
        Description = "34 bytes but PUSHDATA1, not OP_DATA_32")]
    public void EnforcePayTo_RejectsNonP2trDestination(string scriptPubKeyHex)
    {
        var scriptPubKey = new Script(Convert.FromHexString(scriptPubKeyHex));

        Assert.Throws<ArgumentException>(() => CovenantClaimScript.EnforcePayTo(scriptPubKey));
    }

    /// <summary>
    /// The tagged hash of the ArkadeScript is the tweak scalar; if this drifts,
    /// every leaf built from it commits to a key the emulator does not control.
    /// </summary>
    [Test]
    public void ArkadeScriptHash_MatchesGoImplementation()
    {
        var script = CovenantClaimScript.EnforcePayTo(ReceiverPkScript);

        Assert.That(
            Convert.ToHexString(ArkadeTweak.ComputeScriptHash(script)).ToLowerInvariant(),
            Is.EqualTo(ExpectedArkadeScriptHashHex));
    }

    [Test]
    public void EmulatorTweakedKey_MatchesGoImplementation()
    {
        var script = CovenantClaimScript.EnforcePayTo(ReceiverPkScript);
        var tweaked = ArkadeTweak.Tweak(EmulatorTaprootPubKey, script);

        Assert.That(
            Convert.ToHexString(tweaked.ToBytes()).ToLowerInvariant(),
            Is.EqualTo(ExpectedEmulatorTweakedXOnlyHex));
    }
}
