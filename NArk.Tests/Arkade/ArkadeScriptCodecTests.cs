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
}
