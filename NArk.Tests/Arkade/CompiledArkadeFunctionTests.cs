using NArk.Abstractions.Extensions;
using NArk.Arkade.Contracts;
using NArk.Arkade.Program;
using NArk.Arkade.Program.Models;
using NArk.Arkade.Scripts;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Tests.Arkade;

/// <summary>
/// Covers <see cref="CompiledArkadeFunction"/> and the <see cref="ArkadeProgramFunctionScriptBuilder"/>
/// it materializes, exercised through the natural flow: build an <see cref="ArkProgramContract"/> for a
/// covenant program → read its compiled functions → materialize each into its script builder. This is
/// the same compile → contract → script-builder path <see cref="ArkProgramContractTransformer"/> drives
/// when producing a spendable coin.
/// </summary>
[TestFixture]
public class CompiledArkadeFunctionTests
{
    private static readonly OutputDescriptor TestServerKey =
        KeyExtensions.ParseOutputDescriptor(
            "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88", Network.RegTest);

    private static readonly OutputDescriptor TestUserKey =
        KeyExtensions.ParseOutputDescriptor(
            "030192e796452d6df9697c280542e1560557bcf79a347d925895043136225c7cb4", Network.RegTest);

    private static ECXOnlyPubKey EmulatorKey()
    {
        var seed = Enumerable.Repeat((byte)0x24, 32).ToArray();
        return ECXOnlyPubKey.Create(new Key(seed).PubKey.TaprootInternalKey.ToBytes());
    }

    // A one-leaf covenant: the spend must send output 0's value to exactly $amount. The ScriptSegment
    // makes it an Arkade (emulator-cosigned) function, so it compiles an arkade-script + emulator key.
    private static ArkProgramContract CovenantContract() =>
        new(TestServerKey,
            new ArkadeProgram
            {
                Version = ArkadeProgram.SupportedVersion,
                Params =
                [
                    new TypedInput { Name = "server", Type = InputType.Pubkey },
                    new TypedInput { Name = "user", Type = InputType.Pubkey },
                    new TypedInput { Name = "amount", Type = InputType.Int },
                ],
                Functions = new Dictionary<string, ArkadeFunction>
                {
                    ["claim"] = new()
                    {
                        Tapscript = new TapscriptSegment
                        {
                            Signers = [AsmToken.FromText("$server"), AsmToken.FromText("$user")],
                        },
                        ScriptSegment = new ArkadeScriptSegment
                        {
                            Asm = ["INSPECTOUTPUTVALUE", "$amount", "EQUAL"],
                            Witness = [0],
                        },
                    },
                },
            },
            new Dictionary<string, AsmToken> { ["amount"] = 10_000 },
            user: TestUserKey,
            emulatorKey: EmulatorKey());

    [Test]
    public void CompiledFunction_ExposesNameDefinitionSignersAndArkadeScript()
    {
        var fn = CovenantContract().FunctionByName("claim");

        Assert.That(fn, Is.Not.Null);
        Assert.That(fn!.Name, Is.EqualTo("claim"));
        Assert.That(fn.Definition.ScriptSegment, Is.Not.Null);
        Assert.That(fn.LeafScript, Is.Not.Empty);
        // A covenant function carries its own arkade-script bytes + the emulator key it's tweaked against.
        Assert.That(fn.ArkadeScriptBytes, Is.Not.Null.And.Not.Empty);
        Assert.That(fn.EmulatorKey, Is.Not.Null);
        // $server + $user both resolve into the leaf's signer set.
        Assert.That(fn.SignerKeys, Has.Count.EqualTo(2));
    }

    [Test]
    public void UnknownFunctionName_ReturnsNull()
        => Assert.That(CovenantContract().FunctionByName("does-not-exist"), Is.Null);

    [Test]
    public void ToScriptBuilder_ProducesArkadeBoundBuilder_CarryingScriptAndEmulatorKey()
    {
        var fn = CovenantContract().FunctionByName("claim")!;

        var builder = fn.ToScriptBuilder();

        Assert.That(builder, Is.InstanceOf<ArkadeProgramFunctionScriptBuilder>());
        var bound = (IArkadeBoundScriptBuilder)builder;
        Assert.That(bound.ArkadeScript, Is.EqualTo(fn.ArkadeScriptBytes));
        Assert.That(bound.EmulatorKeys, Is.Not.Empty);
        // No witness supplied → nothing to co-sign the arkade-script with yet.
        Assert.That(bound.ArkadeScriptWitness, Is.Null);
    }

    [Test]
    public void ToScriptBuilder_WithWitness_CarriesArkadeScriptWitness()
    {
        var fn = CovenantContract().FunctionByName("claim")!;
        var witness = new WitScript(Op.GetPushOp(Convert.FromHexString("00"))); // output index 0

        var bound = (IArkadeBoundScriptBuilder)fn.ToScriptBuilder(witness);

        Assert.That(bound.ArkadeScriptWitness, Is.EqualTo(witness));
        // The on-chain leaf script is unaffected by the arkade-script witness.
        Assert.That(bound.ArkadeScript, Is.EqualTo(fn.ArkadeScriptBytes));
    }
}
