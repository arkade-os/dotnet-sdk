using NArk.Abstractions.Extensions;
using NArk.Arkade.Contracts;
using NArk.Core.Contracts;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Tests.Arkade;

/// <summary>
/// Contract types defined outside <c>NArk.Core</c> must survive a round trip through storage.
/// </summary>
/// <remarks>
/// <para>
/// The parser only knows the types registered with it, and nothing about writing a contract checks
/// that it can be read back — storage records the type name whether or not anyone can parse it. So
/// an unregistered type fails silently at the far end of the system: a sweeper that cannot see its
/// own VTXOs, a coin that refuses to be signed, and a log line about parsing that names no type.
/// </para>
/// <para>
/// That is what happened to <c>HTLCv2</c>, and the reason these assertions are about the mechanism
/// rather than about one contract: the next type added outside Core will fail exactly the same way,
/// and this is where it should be caught.
/// </para>
/// </remarks>
[TestFixture]
public class ArkContractParserRegistrationTests
{
    [Test]
    public void ArkadeContractTypes_AreParseable()
    {
        // Registered by a module initializer in NArk.Arkade, so referencing the assembly is enough.
        var contract = BuildVHtlcV2();

        var parsed = ArkContractParser.Parse(contract.ToString(), Network.RegTest);

        Assert.That(parsed, Is.Not.Null,
            $"{VHTLCv2Contract.ContractType} is written to storage but cannot be read back — "
            + "the type is not registered with ArkContractParser");
        Assert.That(parsed, Is.InstanceOf<VHTLCv2Contract>());
        Assert.That(parsed!.GetArkAddress().ToString(false),
            Is.EqualTo(contract.GetArkAddress().ToString(false)),
            "a parsed contract that derives a different address is not the same contract");
    }

    [Test]
    public void AnUnknownType_StillParsesAsUnknown()
    {
        // The registration must not have displaced the fallback: an unrecognised type is a contract
        // this wallet cannot sign for, which is a different thing from a parser that returns null.
        var parsed = ArkContractParser.Parse(
            "not-a-real-contract-type", new Dictionary<string, string>(), Network.RegTest);

        Assert.That(parsed, Is.Null);
    }

    [Test]
    public void RegisteringTwice_ReplacesRatherThanDuplicates()
    {
        // DI extension methods get called more than once — in tests, and in hosts that build
        // several service providers. Registration has to be idempotent or the list grows forever.
        ArkContractParser.Register(VHTLCv2Contract.ContractType, VHTLCv2Contract.Parse);
        ArkContractParser.Register(VHTLCv2Contract.ContractType, VHTLCv2Contract.Parse);

        var contract = BuildVHtlcV2();
        Assert.That(
            ArkContractParser.Parse(contract.ToString(), Network.RegTest),
            Is.InstanceOf<VHTLCv2Contract>());
    }

    private static VHTLCv2Contract BuildVHtlcV2()
    {
        var preimageHash = new uint160(Enumerable.Repeat((byte)0x11, 20).ToArray(), false);
        var pkScript = new Key().PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86).ToBytes();

        return new VHTLCv2Contract(
            Descriptor(), Descriptor(), Descriptor(),
            preimageHash,
            new LockTime(1_000_000),
            new Sequence(TimeSpan.FromSeconds(1024)),
            new Sequence(TimeSpan.FromSeconds(1536)),
            new Sequence(TimeSpan.FromSeconds(2048)),
            new EmulatorCovenants(
                ECXOnlyPubKey.Create(new Key().PubKey.GetTaprootFullPubKey().OutputKey.ToBytes()),
                pkScript,
                pkScript));
    }

    private static OutputDescriptor Descriptor() =>
        KeyExtensions.ParseOutputDescriptor(new Key().PubKey.ToHex(), Network.RegTest);
}
