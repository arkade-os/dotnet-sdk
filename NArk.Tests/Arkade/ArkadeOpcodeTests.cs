using NArk.Arkade.Scripts;

namespace NArk.Tests.Arkade;

/// <summary>
/// Pins <see cref="ArkadeOpcode"/> / <see cref="ArkadeOpcodeRegistry"/> to the
/// <c>arkade-os/emulator</c> opcode table (<c>pkg/arkade/opcode.go</c>) — the
/// deployed VM that actually executes scripts, hence the authority on what each
/// byte means. ts-sdk #319 disagrees on <c>0xd7–0xe2</c> (it lists 64-bit
/// arithmetic / conversion opcodes there); the emulator wins because a script
/// built with the wrong byte would silently execute as a different opcode.
/// </summary>
[TestFixture]
public class ArkadeOpcodeTests
{
    // (byte, canonical OP_ name) verbatim from arkade-os/emulator pkg/arkade/opcode.go.
    private static readonly (byte Value, string Name)[] EmulatorOps =
    {
        (0xb3, "OP_MERKLEBRANCHVERIFY"),
        (0xc4, "OP_SHA256INITIALIZE"), (0xc5, "OP_SHA256UPDATE"), (0xc6, "OP_SHA256FINALIZE"),
        (0xc7, "OP_INSPECTINPUTOUTPOINT"), (0xc8, "OP_INSPECTINPUTARKADESCRIPTHASH"),
        (0xc9, "OP_INSPECTINPUTVALUE"), (0xca, "OP_INSPECTINPUTSCRIPTPUBKEY"),
        (0xcb, "OP_INSPECTINPUTSEQUENCE"), (0xcc, "OP_CHECKSIGFROMSTACK"),
        (0xcd, "OP_PUSHCURRENTINPUTINDEX"), (0xce, "OP_INSPECTINPUTARKADEWITNESSHASH"),
        (0xcf, "OP_INSPECTOUTPUTVALUE"), (0xd1, "OP_INSPECTOUTPUTSCRIPTPUBKEY"),
        (0xd2, "OP_INSPECTVERSION"), (0xd3, "OP_INSPECTLOCKTIME"),
        (0xd4, "OP_INSPECTNUMINPUTS"), (0xd5, "OP_INSPECTNUMOUTPUTS"), (0xd6, "OP_TXWEIGHT"),
        // 0xd7–0xe2 — emulator-authoritative (byte-string + EC ops), NOT 64-bit arithmetic.
        (0xd7, "OP_NUM2BIN"), (0xd8, "OP_BIN2NUM"), (0xd9, "OP_REVERSEBYTES"), (0xda, "OP_MODEXP"),
        (0xe0, "OP_ECADD"), (0xe1, "OP_ECMUL"), (0xe2, "OP_ECPAIRING"),
        (0xe3, "OP_ECMULSCALARVERIFY"), (0xe4, "OP_TWEAKVERIFY"),
        // Asset groups 0xe5–0xf2.
        (0xe5, "OP_INSPECTNUMASSETGROUPS"), (0xe6, "OP_INSPECTASSETGROUPASSETID"),
        (0xe7, "OP_INSPECTASSETGROUPCTRL"), (0xe8, "OP_FINDASSETGROUPBYASSETID"),
        (0xe9, "OP_INSPECTASSETGROUPMETADATAHASH"), (0xea, "OP_INSPECTASSETGROUPNUM"),
        (0xeb, "OP_INSPECTASSETGROUP"), (0xec, "OP_INSPECTASSETGROUPSUM"),
        (0xed, "OP_INSPECTOUTASSETCOUNT"), (0xee, "OP_INSPECTOUTASSETAT"),
        (0xef, "OP_INSPECTOUTASSETLOOKUP"), (0xf0, "OP_INSPECTINASSETCOUNT"),
        (0xf1, "OP_INSPECTINASSETAT"), (0xf2, "OP_INSPECTINASSETLOOKUP"),
        // Tx id + packet introspection + sighash.
        (0xf3, "OP_TXID"), (0xf4, "OP_INSPECTPACKET"), (0xf5, "OP_INSPECTINPUTPACKET"),
        (0xf6, "OP_SIGHASH"),
    };

    [Test]
    public void Registry_MapsEmulatorOpcodeBytesToNamesBothWays()
    {
        Assert.Multiple(() =>
        {
            foreach (var (value, name) in EmulatorOps)
            {
                Assert.That(ArkadeOpcodeRegistry.GetOpcodeName(value), Is.EqualTo(name),
                    $"byte 0x{value:x2} → name");
                Assert.That(ArkadeOpcodeRegistry.GetOpcodeValue(name), Is.EqualTo(value),
                    $"name {name} → byte");
                Assert.That(ArkadeOpcodeRegistry.IsArkadeOpcode(value), Is.True,
                    $"0x{value:x2} should be an Arkade opcode");
            }
        });
    }

    [Test]
    public void TsSdk64BitArithmeticOpcodes_AreNotDefined()
    {
        // The emulator has no 64-bit arithmetic / scriptnum-conversion opcodes;
        // 0xd7–0xe2 are byte-string and EC ops. None of these ts-sdk #319 names
        // may resolve to a byte in the .NET registry.
        Assert.Multiple(() =>
        {
            foreach (var name in new[]
            {
                "OP_ADD64", "OP_SUB64", "OP_MUL64", "OP_DIV64", "OP_NEG64",
                "OP_LESSTHAN64", "OP_LESSTHANOREQUAL64", "OP_GREATERTHAN64",
                "OP_GREATERTHANOREQUAL64", "OP_SCRIPTNUMTOLE64", "OP_LE64TOSCRIPTNUM",
                "OP_LE32TOLE64",
            })
                Assert.That(ArkadeOpcodeRegistry.GetOpcodeValue(name), Is.Null, name);
        });
    }
}
