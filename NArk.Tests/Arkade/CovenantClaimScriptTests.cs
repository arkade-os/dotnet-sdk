using NArk.Arkade.Covclaim;
using NArk.Arkade.Crypto;
using NArk.Arkade.Scripts;
using NArk.Core.Enums;
using NArk.Core.Scripts;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Tests.Arkade;

/// <summary>
/// Byte-compatibility tests for the covclaimd covenant-claim primitives.
/// </summary>
/// <remarks>
/// <para>
/// Every expected value here was produced by running the canonical Go
/// implementations — covclaimd's <c>preimage.EnforcePayTo</c> /
/// <c>preimage.CovenantClaimClosure</c> and arkd's
/// <c>script.ConditionMultisigClosure.Script()</c> — over the same inputs.
/// Pinning them matters more than usual: the ArkadeScript bytes are tagged-hashed
/// into the emulator's signing key, so a single byte of drift produces a leaf the
/// emulator will never co-sign, and the failure is silent — the claim simply
/// never happens and the swap quietly times out.
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

    private const string ServerPubKeyHex =
        "02c6047f9441ed7d6d3045406e95c07cd85c778e4b8cef3ca7abac09b95c709ee5";

    private const string EmulatorPubKeyHex =
        "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";

    private const string PreimageHash160Hex = "f1e2d3c4b5a6978869504132231405f6e7d8c9ba";

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

    /// <summary>
    /// OP_HASH160 &lt;hash&gt; OP_EQUAL OP_VERIFY &lt;server&gt; OP_CHECKSIGVERIFY
    /// &lt;emulatorTweaked&gt; OP_CHECKSIG
    /// </summary>
    private const string ExpectedConditionMultisigLeafHex =
        "a914f1e2d3c4b5a6978869504132231405f6e7d8c9ba87" +
        "6920c6047f9441ed7d6d3045406e95c07cd85c778e4b8cef3ca7abac09b95c709ee5ad" +
        "2077a2e768588b5ced39c389e2ce803041bf9a70d503b34b49edf5970d912dcbb1ac";

    private static Script ReceiverPkScript => new(Convert.FromHexString(ReceiverPkScriptHex));

    private static ECXOnlyPubKey ServerXOnly =>
        ParsePubKey(ServerPubKeyHex).ToXOnlyPubKey();

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

    /// <summary>
    /// The whole point of the client: build the exact leaf covclaimd searches the
    /// taptree for. A mismatch here means every reveal is rejected.
    /// </summary>
    [Test]
    public void ConditionMultisigLeaf_MatchesGoImplementation()
    {
        var leaf = BuildCovenantClaimLeaf();

        Assert.That(
            Convert.ToHexString(leaf.Build().Script.ToBytes()).ToLowerInvariant(),
            Is.EqualTo(ExpectedConditionMultisigLeafHex));
    }

    /// <summary>
    /// arkd's <c>MultisigClosure</c> terminates the owner set with OP_CHECKSIG and
    /// uses OP_CHECKSIGVERIFY for everything before it. Ordering is load-bearing —
    /// the emulator key must be last.
    /// </summary>
    [Test]
    public void ConditionMultisigLeaf_PutsEmulatorKeyLastUnderChecksig()
    {
        var ops = BuildCovenantClaimLeaf().BuildScript().ToList();

        Assert.Multiple(() =>
        {
            Assert.That(ops[^1].Code, Is.EqualTo(OpcodeType.OP_CHECKSIG));
            Assert.That(
                Convert.ToHexString(ops[^2].PushData!).ToLowerInvariant(),
                Is.EqualTo(ExpectedEmulatorTweakedXOnlyHex));
            Assert.That(ops[^3].Code, Is.EqualTo(OpcodeType.OP_CHECKSIGVERIFY));
        });
    }

    [Test]
    public void ConditionMultisigLeaf_ExposesEmulatorKeysForDispatch()
    {
        var leaf = BuildCovenantClaimLeaf();

        Assert.Multiple(() =>
        {
            Assert.That(leaf.EmulatorKeys, Has.Count.EqualTo(1));
            Assert.That(leaf.EmulatorKeys[0], Is.EqualTo(EmulatorTaprootPubKey));
            Assert.That(leaf.ArkadeScript, Is.EqualTo(CovenantClaimScript.EnforcePayTo(ReceiverPkScript)));
        });
    }

    [Test]
    public void ConditionMultisigLeaf_RejectsEmptyEmulatorSet()
    {
        Assert.Throws<ArgumentException>(() => new ArkadeConditionMultisigTapScript(
            CovenantClaimScript.EnforcePayTo(ReceiverPkScript),
            new HashLockTapScript(Convert.FromHexString(PreimageHash160Hex), HashLockTypeOption.Hash160),
            [ServerXOnly],
            []));
    }

    private static ArkadeConditionMultisigTapScript BuildCovenantClaimLeaf() =>
        new(
            CovenantClaimScript.EnforcePayTo(ReceiverPkScript),
            new CompositeTapScript(
                new HashLockTapScript(Convert.FromHexString(PreimageHash160Hex), HashLockTypeOption.Hash160)),
            [ServerXOnly],
            [EmulatorTaprootPubKey]);
}
