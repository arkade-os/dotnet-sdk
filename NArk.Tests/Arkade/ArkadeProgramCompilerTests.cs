using NArk.Arkade.Crypto;
using NArk.Arkade.Program;
using NArk.Arkade.Scripts;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Tests.Arkade;

[TestFixture]
public class ArkadeProgramCompilerTests
{
    [Test]
    public void PlainMultisig_CompilesToServerCheckSig()
    {
        var (server, _, _) = GenerateThreeKeys();
        var program = new ArkadeProgram
        {
            Version = ArkadeProgram.SupportedVersion,
            Functions = new Dictionary<string, ArkadeFunction>
            {
                ["exit"] = new()
                {
                    Tapscript = new ArkadeTapscriptSegment { Signers = [ArkadeToken.FromText("server")] },
                },
            },
        };

        var compiled = ArkadeProgramCompiler.Compile(
            program, new Dictionary<string, ArkadeToken>(), new ArkadeProgramKeys { ServerKey = server });

        Assert.That(compiled, Has.Count.EqualTo(1));
        var fn = compiled[0];
        Assert.That(fn.Name, Is.EqualTo("exit"));
        Assert.That(fn.ArkadeScriptBytes, Is.Null);

        var ops = ArkadeScript.Decode(fn.LeafScript);
        Assert.That(ops, Has.Count.EqualTo(2));
        Assert.That(ops[0].ToBytes(), Is.EqualTo(Op.GetPushOp(server.ToBytes()).ToBytes()));
        Assert.That(ops[1].Code, Is.EqualTo(OpcodeType.OP_CHECKSIG));
    }

    [Test]
    public void CsvSegment_PrependsSequenceGate()
    {
        var (server, _, _) = GenerateThreeKeys();
        var sequence = new Sequence(144);
        var program = new ArkadeProgram
        {
            Version = ArkadeProgram.SupportedVersion,
            Functions = new Dictionary<string, ArkadeFunction>
            {
                ["refund"] = new()
                {
                    Tapscript = new ArkadeTapscriptSegment
                    {
                        Signers = [ArkadeToken.FromText("server")],
                        Csv = sequence,
                    },
                },
            },
        };

        var compiled = ArkadeProgramCompiler.Compile(
            program, new Dictionary<string, ArkadeToken>(), new ArkadeProgramKeys { ServerKey = server });

        var ops = ArkadeScript.Decode(compiled[0].LeafScript);
        Assert.That(ops[0].ToBytes(), Is.EqualTo(Op.GetPushOp(sequence.Value).ToBytes()));
        Assert.That(ops[1].Code, Is.EqualTo(OpcodeType.OP_CHECKSEQUENCEVERIFY));
        Assert.That(ops[2].Code, Is.EqualTo(OpcodeType.OP_DROP));
        Assert.That(ops[^1].Code, Is.EqualTo(OpcodeType.OP_CHECKSIG));
    }

    [Test]
    public void CovenantSegment_AppendsTweakedEmulatorKey()
    {
        var (server, _, emulator) = GenerateThreeKeys();
        var emulatorXOnly = ECXOnlyPubKey.Create(emulator.ToBytes());
        var program = new ArkadeProgram
        {
            Version = ArkadeProgram.SupportedVersion,
            Functions = new Dictionary<string, ArkadeFunction>
            {
                ["claim"] = new()
                {
                    Tapscript = new ArkadeTapscriptSegment { Signers = [ArkadeToken.FromText("server")] },
                    CovenantSegment = new ArkadeCovenantSegment
                    {
                        Asm = [ArkadeToken.FromText("OP_TXID")],
                    },
                },
            },
        };

        var compiled = ArkadeProgramCompiler.Compile(
            program,
            new Dictionary<string, ArkadeToken>(),
            new ArkadeProgramKeys { ServerKey = server, EmulatorKey = emulatorXOnly });

        var fn = compiled[0];
        Assert.That(fn.ArkadeScriptBytes, Is.Not.Null);

        var expectedTweak = ArkadeTweak.Tweak(emulator, fn.ArkadeScriptBytes!);
        var ops = ArkadeScript.Decode(fn.LeafScript);

        // <server> CHECKSIGVERIFY <tweaked-emulator> CHECKSIG
        Assert.That(ops[0].ToBytes(), Is.EqualTo(Op.GetPushOp(server.ToBytes()).ToBytes()));
        Assert.That(ops[1].Code, Is.EqualTo(OpcodeType.OP_CHECKSIGVERIFY));
        Assert.That(ops[2].ToBytes(), Is.EqualTo(Op.GetPushOp(expectedTweak.ToBytes()).ToBytes()));
        Assert.That(ops[3].Code, Is.EqualTo(OpcodeType.OP_CHECKSIG));
    }

    [Test]
    public void CovenantSegment_ToScriptBuilder_IsArkadeBound()
    {
        var (server, _, emulator) = GenerateThreeKeys();
        var emulatorXOnly = ECXOnlyPubKey.Create(emulator.ToBytes());
        var program = new ArkadeProgram
        {
            Version = ArkadeProgram.SupportedVersion,
            Functions = new Dictionary<string, ArkadeFunction>
            {
                ["claim"] = new()
                {
                    Tapscript = new ArkadeTapscriptSegment { Signers = [ArkadeToken.FromText("server")] },
                    CovenantSegment = new ArkadeCovenantSegment { Asm = [ArkadeToken.FromText("OP_TXID")] },
                },
            },
        };

        var compiled = ArkadeProgramCompiler.Compile(
            program,
            new Dictionary<string, ArkadeToken>(),
            new ArkadeProgramKeys { ServerKey = server, EmulatorKey = emulatorXOnly });

        var builder = compiled[0].ToScriptBuilder();

        Assert.That(builder, Is.InstanceOf<IArkadeBoundScriptBuilder>());
        var arkadeBound = (IArkadeBoundScriptBuilder)builder;
        Assert.That(arkadeBound.ArkadeScript, Is.EqualTo(compiled[0].ArkadeScriptBytes));
        Assert.That(arkadeBound.EmulatorKeys, Has.Count.EqualTo(1));
        Assert.That(arkadeBound.EmulatorKeys[0].ToBytes(), Is.EqualTo(emulator.ToBytes()));
        Assert.That(builder.BuildScript().Select(o => o.ToBytes()),
            Is.EqualTo(ArkadeScript.Decode(compiled[0].LeafScript).Select(o => o.ToBytes())));
    }

    [Test]
    public void PlainSegment_ToScriptBuilder_IsNotArkadeBound()
    {
        var (server, _, _) = GenerateThreeKeys();
        var program = new ArkadeProgram
        {
            Version = ArkadeProgram.SupportedVersion,
            Functions = new Dictionary<string, ArkadeFunction>
            {
                ["exit"] = new()
                {
                    Tapscript = new ArkadeTapscriptSegment { Signers = [ArkadeToken.FromText("server")] },
                },
            },
        };

        var compiled = ArkadeProgramCompiler.Compile(
            program, new Dictionary<string, ArkadeToken>(), new ArkadeProgramKeys { ServerKey = server });

        var builder = compiled[0].ToScriptBuilder();
        Assert.That(builder, Is.Not.InstanceOf<IArkadeBoundScriptBuilder>());
    }

    [Test]
    public void CovenantSegment_WithoutEmulatorKey_Throws()
    {
        var (server, _, _) = GenerateThreeKeys();
        var program = new ArkadeProgram
        {
            Version = ArkadeProgram.SupportedVersion,
            Functions = new Dictionary<string, ArkadeFunction>
            {
                ["claim"] = new()
                {
                    Tapscript = new ArkadeTapscriptSegment { Signers = [ArkadeToken.FromText("server")] },
                    CovenantSegment = new ArkadeCovenantSegment { Asm = [ArkadeToken.FromText("OP_TXID")] },
                },
            },
        };

        Assert.Throws<InvalidOperationException>(() => ArkadeProgramCompiler.Compile(
            program, new Dictionary<string, ArkadeToken>(), new ArkadeProgramKeys { ServerKey = server }));
    }

    [Test]
    public void ParamSubstitution_ResolvesHashInCondition()
    {
        var (server, _, _) = GenerateThreeKeys();
        var hash = Convert.FromHexString("4d487dd3753a89bc9fe98401d1196523058251fc");
        var program = new ArkadeProgram
        {
            Version = ArkadeProgram.SupportedVersion,
            Functions = new Dictionary<string, ArkadeFunction>
            {
                ["claim"] = new()
                {
                    Tapscript = new ArkadeTapscriptSegment
                    {
                        Signers = [ArkadeToken.FromText("server")],
                        Asm =
                        [
                            ArkadeToken.FromText("HASH160"),
                            ArkadeToken.FromText("$hash"),
                            ArkadeToken.FromText("EQUALVERIFY"),
                        ],
                    },
                },
            },
        };

        var compiled = ArkadeProgramCompiler.Compile(
            program,
            new Dictionary<string, ArkadeToken> { ["hash"] = ArkadeToken.FromBytes(hash) },
            new ArkadeProgramKeys { ServerKey = server });

        var ops = ArkadeScript.Decode(compiled[0].LeafScript);
        Assert.That(ops[0].Code, Is.EqualTo(OpcodeType.OP_HASH160));
        Assert.That(ops[1].PushData, Is.EqualTo(hash));
        Assert.That(ops[2].Code, Is.EqualTo(OpcodeType.OP_EQUALVERIFY));
        Assert.That(ops[3].Code, Is.EqualTo(OpcodeType.OP_VERIFY));
    }

    [Test]
    public void ArkadeOpcodeInTapscriptAsm_Throws()
    {
        var (server, _, _) = GenerateThreeKeys();
        var program = new ArkadeProgram
        {
            Version = ArkadeProgram.SupportedVersion,
            Functions = new Dictionary<string, ArkadeFunction>
            {
                ["claim"] = new()
                {
                    Tapscript = new ArkadeTapscriptSegment
                    {
                        Signers = [ArkadeToken.FromText("server")],
                        Asm = [ArkadeToken.FromText("OP_TXID")],
                    },
                },
            },
        };

        Assert.Throws<InvalidOperationException>(() => ArkadeProgramCompiler.Compile(
            program, new Dictionary<string, ArkadeToken>(), new ArkadeProgramKeys { ServerKey = server }));
    }

    [Test]
    public void AsmAndCsvTogether_Throws()
    {
        var (server, _, _) = GenerateThreeKeys();
        var program = new ArkadeProgram
        {
            Version = ArkadeProgram.SupportedVersion,
            Functions = new Dictionary<string, ArkadeFunction>
            {
                ["claim"] = new()
                {
                    Tapscript = new ArkadeTapscriptSegment
                    {
                        Signers = [ArkadeToken.FromText("server")],
                        Asm = [ArkadeToken.FromText("OP_1")],
                        Csv = new Sequence(1),
                    },
                },
            },
        };

        Assert.Throws<InvalidOperationException>(() => ArkadeProgramCompiler.Compile(
            program, new Dictionary<string, ArkadeToken>(), new ArkadeProgramKeys { ServerKey = server }));
    }

    private static (ECXOnlyPubKey server, ECXOnlyPubKey user, TaprootPubKey emulator) GenerateThreeKeys()
    {
        var rng = new Random(42);
        ECXOnlyPubKey Make()
        {
            var seed = new byte[32];
            rng.NextBytes(seed);
            var bytes = new Key(seed).PubKey.TaprootInternalKey.ToBytes();
            return ECXOnlyPubKey.Create(bytes);
        }
        var emulatorSeed = new byte[32];
        rng.NextBytes(emulatorSeed);
        var emulator = new Key(emulatorSeed).PubKey.GetTaprootFullPubKey().OutputKey;
        return (Make(), Make(), emulator);
    }
}
