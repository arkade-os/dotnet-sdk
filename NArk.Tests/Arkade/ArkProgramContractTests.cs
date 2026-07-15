using NArk.Abstractions.Extensions;
using NArk.Arkade.Contracts;
using NArk.Arkade.Program;
using NBitcoin;

namespace NArk.Tests.Arkade;

[TestFixture]
public class ArkProgramContractTests
{
    private static readonly string ServerHex = "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88";
    private static readonly string EmulatorHex = "030192e796452d6df9697c280542e1560557bcf79a347d925895043136225c7cb4";

    [Test]
    public void SimpleExitProgram_ProducesAValidAddress()
    {
        var server = KeyExtensions.ParseOutputDescriptor(ServerHex, Network.RegTest);
        var program = new ArkadeProgram
        {
            Version = ArkadeProgram.SupportedVersion,
            Params = ["server"],
            Functions = new Dictionary<string, ArkadeFunction>
            {
                ["exit"] = new()
                {
                    Tapscript = new TapscriptSegment { Signers = [AsmToken.FromText("$server")] },
                },
            },
        };

        var contract = new ArkProgramContract(server, program, new Dictionary<string, AsmToken>());

        Assert.That(contract.Type, Is.EqualTo(ArkProgramContract.ContractType));
        Assert.That(contract.GetArkAddress().ToString(false), Does.StartWith("tark1"));
    }

    [Test]
    public void CovenantProgram_RoundTripsThroughContractData_WithSameAddress()
    {
        var server = KeyExtensions.ParseOutputDescriptor(ServerHex, Network.RegTest);
        var emulator = KeyExtensions.ParseOutputDescriptor(EmulatorHex, Network.RegTest);
        var program = new ArkadeProgram
        {
            Version = ArkadeProgram.SupportedVersion,
            Params = ["server", "hash"],
            Functions = new Dictionary<string, ArkadeFunction>
            {
                ["claim"] = new()
                {
                    Inputs = [new TypedInput() { Name = "preimage", Type = InputType.Bytes }],
                    Tapscript = new TapscriptSegment
                    {
                        Signers = [AsmToken.FromText("$server")],
                        Asm =
                        [
                            AsmToken.FromText("HASH160"),
                            AsmToken.FromText("$hash"),
                            AsmToken.FromText("EQUALVERIFY"),
                        ],
                    },
                    ScriptSegment = new ArkadeScriptSegment { Asm = [AsmToken.FromText("OP_TXID")] },
                },
            },
        };
        var hash = Convert.FromHexString("4d487dd3753a89bc9fe98401d1196523058251fc");
        var args = new Dictionary<string, AsmToken> { ["hash"] = AsmToken.FromBytes(hash) };

        var original = new ArkProgramContract(server, program, args, emulatorKey: emulator.ToXOnlyPubKey());
        var address = original.GetArkAddress().ToString(false);

        var contractData = original.ToEntity("test-wallet").AdditionalData;
        var restored = ArkProgramContract.Parse(contractData, Network.RegTest);

        Assert.That(restored.GetArkAddress().ToString(false), Is.EqualTo(address));
    }

    [Test]
    public void TypedParams_RoundTripThroughContractData_DecodeArgsByType()
    {
        var server = KeyExtensions.ParseOutputDescriptor(ServerHex, Network.RegTest);
        var hash = Convert.FromHexString("4d487dd3753a89bc9fe98401d1196523058251fc");
        var program = new ArkadeProgram
        {
            Version = ArkadeProgram.SupportedVersion,
            // Typed params make the list authoritative and drive type-aware arg decoding.
            Params =
            [
                new TypedInput { Name = "server", Type = InputType.Pubkey },
                new TypedInput { Name = "hash", Type = InputType.Hash },
                new TypedInput { Name = "amount", Type = InputType.Int },
            ],
            Functions = new Dictionary<string, ArkadeFunction>
            {
                ["claim"] = new()
                {
                    Tapscript = new TapscriptSegment
                    {
                        Signers = [AsmToken.FromText("$server")],
                        Asm = ["HASH160", "$hash", "EQUALVERIFY"],
                    },
                },
            },
        };
        // $server auto-binds from the contract's key; hash (bytes) and amount (int) are explicit.
        var args = new Dictionary<string, AsmToken>
        {
            ["hash"] = AsmToken.FromBytes(hash),
            ["amount"] = AsmToken.FromNumber(10_000),
        };

        var original = new ArkProgramContract(server, program, args);
        var address = original.GetArkAddress().ToString(false);

        var contractData = original.ToEntity("test-wallet").AdditionalData;
        var restored = ArkProgramContract.Parse(contractData, Network.RegTest);

        Assert.That(restored.GetArkAddress().ToString(false), Is.EqualTo(address));
        // The int arg came back a Number (not misread as bytes), the hash a Bytes token.
        Assert.That(restored.Args["amount"].Kind, Is.EqualTo(AsmTokenKind.Number));
        Assert.That(restored.Args["amount"].Number, Is.EqualTo(new System.Numerics.BigInteger(10_000)));
        Assert.That(restored.Args["hash"].Kind, Is.EqualTo(AsmTokenKind.Bytes));
        Assert.That(restored.Args["hash"].Bytes, Is.EqualTo(hash));
    }
}
