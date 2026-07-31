using NArk.Arkade.Scripts;
using NBitcoin;

namespace NArk.Tests.Arkade;

/// <summary>
/// Round-trip + ASM tests for <see cref="ArkadeScript"/>. The encoder/decoder
/// is mostly a pass-through over NBitcoin's <see cref="Script"/>, so the
/// useful coverage here is making sure (a) Arkade extension opcodes survive
/// the trip without being confused for data pushes, and (b) ASM formatting
/// uses the canonical Arkade names (<c>OP_INSPECTOUTPUTVALUE</c>, etc.).
/// </summary>
[TestFixture]
public class ArkadeScriptCodecTests
{
    [Test]
    public void EncodeDecode_PreservesArkadeOpcodes()
    {
        // OP_DUP, OP_HASH160, push 20 zero bytes, OP_EQUALVERIFY, OP_INSPECTOUTPUTVALUE
        Op[] ops =
        [
            OpcodeType.OP_DUP,
            OpcodeType.OP_HASH160,
            Op.GetPushOp(new byte[20]),
            OpcodeType.OP_EQUALVERIFY,
            (OpcodeType)(byte)ArkadeOpcode.OP_INSPECTOUTPUTVALUE,
        ];

        var bytes = ArkadeScript.Encode(ops);
        var decoded = ArkadeScript.Decode(bytes);

        // First and last opcodes must match by code; the push survives byte-equal.
        Assert.That(decoded, Has.Count.EqualTo(ops.Length));
        Assert.That(decoded[0].Code, Is.EqualTo(OpcodeType.OP_DUP));
        Assert.That(decoded[1].Code, Is.EqualTo(OpcodeType.OP_HASH160));
        Assert.That(decoded[2].PushData, Is.EqualTo(new byte[20]));
        Assert.That(decoded[3].Code, Is.EqualTo(OpcodeType.OP_EQUALVERIFY));
        Assert.That((byte)decoded[4].Code, Is.EqualTo((byte)ArkadeOpcode.OP_INSPECTOUTPUTVALUE));
    }

    [Test]
    public void ToAsm_UsesArkadeMnemonics()
    {
        Op[] ops =
        [
            OpcodeType.OP_DUP,
            OpcodeType.OP_HASH160,
            Op.GetPushOp(Convert.FromHexString("deadbeef")),
            OpcodeType.OP_EQUALVERIFY,
            (OpcodeType)(byte)ArkadeOpcode.OP_INSPECTOUTPUTVALUE,
        ];
        var asm = ArkadeScript.ToAsm(ops);
        Assert.That(asm, Is.EqualTo("OP_DUP OP_HASH160 deadbeef OP_EQUALVERIFY OP_INSPECTOUTPUTVALUE"));
    }

    [Test]
    public void FromAsm_RoundTripsThroughBytes()
    {
        const string asm = "OP_DUP OP_HASH160 deadbeef OP_EQUALVERIFY OP_INSPECTOUTPUTVALUE";
        var bytes = ArkadeScript.AsmToBytes(asm);
        Assert.That(ArkadeScript.BytesToAsm(bytes), Is.EqualTo(asm));
    }

    [Test]
    public void FromAsm_AcceptsBareOpcodeNames()
    {
        // The ts-sdk's fromASM accepts both "OP_X" and "X" forms — verify parity.
        var withPrefix = ArkadeScript.FromAsm("OP_DUP OP_INSPECTOUTPUTVALUE");
        var withoutPrefix = ArkadeScript.FromAsm("DUP INSPECTOUTPUTVALUE");
        Assert.That(ArkadeScript.Encode(withPrefix), Is.EqualTo(ArkadeScript.Encode(withoutPrefix)));
    }

    [Test]
    public void FromAsm_RejectsUnknownToken()
    {
        Assert.Throws<FormatException>(() => ArkadeScript.FromAsm("OP_NOT_A_REAL_OPCODE"));
    }

    [Test]
    public void AllArkadeOpcodes_RoundTripThroughAsm()
    {
        // Coverage net: every Arkade enum value emits and re-parses via ASM.
        foreach (var opcode in Enum.GetValues<ArkadeOpcode>())
        {
            var name = opcode.ToString();
            var ops = ArkadeScript.FromAsm(name);
            Assert.That(ops, Has.Count.EqualTo(1), $"ASM round-trip lost {name}");
            Assert.That((byte)ops[0].Code, Is.EqualTo((byte)opcode), $"ASM round-trip mangled {name}");
        }
    }

    // The two tests below pin the invariant that matters for anything that hands a
    // script to the ASM layer and back: going bytes -> ASM -> bytes must not lose
    // information the binary path (Encode/Decode) keeps. They compare against the
    // binary round-trip rather than the literal input so that NBitcoin's own push
    // canonicalisation can't be mistaken for a codec bug.

    [Test]
    public void AsmRoundTrip_PreservesOpZero()
    {
        // OP_0 is how a zero index is expressed (e.g. "inspect output 0"). NBitcoin
        // models it as a push with zero-length data, so rendering it through the push
        // branch produces an empty token that FromAsm's whitespace split then drops —
        // silently deleting the opcode and shifting the script's meaning.
        var script = Convert.FromHexString("00cf");   // OP_0 OP_INSPECTOUTPUTVALUE

        var viaBinary = ArkadeScript.Encode(ArkadeScript.Decode(script));
        var viaAsm = ArkadeScript.AsmToBytes(ArkadeScript.BytesToAsm(script));

        Assert.That(viaAsm, Is.EqualTo(viaBinary));
    }

    [Test]
    public void AsmRoundTrip_PreservesSingleByteDataPushes()
    {
        // A one-byte push renders as bare hex ("11"), which collides with the registry's
        // bare alias for OP_11. Resolving the opcode before the hex fallback turns a push
        // of the byte 0x11 into OP_11 (0x5b) — a valid script with a different meaning.
        //
        // Starts at 0x11 because a one-byte push of 0x01–0x10 is non-minimal (those values
        // have OP_1..OP_16 forms); the ASM path canonicalises them while the binary path
        // preserves the raw bytes, which is a separate and defensible difference.
        for (byte value = 0x11; value <= 0x7f; value++)
        {
            var script = new byte[] { 0x01, value };   // a single-byte data push

            var viaBinary = ArkadeScript.Encode(ArkadeScript.Decode(script));
            var viaAsm = ArkadeScript.AsmToBytes(ArkadeScript.BytesToAsm(script));

            Assert.That(viaAsm, Is.EqualTo(viaBinary), $"ASM round-trip changed a push of 0x{value:x2}");
        }
    }

    [Test]
    public void FromAsm_AcceptsTheBareSmallIntegerFormUsedInTheReadme()
    {
        // The README's ArkadeScript example passes a bare "1" for OP_1. Odd-length numeric
        // aliases can never be mistaken for a hex token, so they must keep resolving even
        // though the even-length ones ("10".."16") are deliberately no longer registered.
        var ops = ArkadeScript.FromAsm(
            "OP_0 OP_INSPECTOUTPUTSCRIPTPUBKEY 1 OP_EQUALVERIFY deadbeef OP_EQUAL");

        Assert.That(ops, Has.Count.EqualTo(6));
        Assert.That(ops[2].Code, Is.EqualTo(OpcodeType.OP_1));
    }
}
