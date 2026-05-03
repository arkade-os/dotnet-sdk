using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Scripts;
using NArk.Arkade.Crypto;
using NArk.Arkade.Introspector;
using NArk.Arkade.Scripts;
using NArk.Core.Assets;
using NArk.Core.Scripts;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NSubstitute;

namespace NArk.Tests.Arkade;

[TestFixture]
public class ArkadePsbtExtensionsTests
{
    [Test]
    public void RequiresIntrospectorCoSigning_TrueWhenAnyArkadeBound()
    {
        var (alice, bob, introspector) = MakeKeys();
        var arkade = new ArkadeNofNMultisigTapScript([0xc4], [alice], [introspector]);
        var plain = new NofNMultisigTapScript([alice, bob]);

        var coinArkade = MakeCoin(alice, bob, arkade);
        var coinPlain = MakeCoin(alice, bob, plain);

        Assert.That(ArkadePsbtExtensions.RequiresIntrospectorCoSigning([coinArkade]), Is.True);
        Assert.That(ArkadePsbtExtensions.RequiresIntrospectorCoSigning([coinPlain]), Is.False);
        // Mixed: still true as long as at least one is arkade.
        Assert.That(ArkadePsbtExtensions.RequiresIntrospectorCoSigning([coinPlain, coinArkade]), Is.True);
    }

    [Test]
    public void BuildIntrospectorOutput_NullWhenNoArkadeCoin()
    {
        var (alice, bob, _) = MakeKeys();
        var plain = new NofNMultisigTapScript([alice, bob]);
        var coin = MakeCoin(alice, bob, plain);

        Assert.That(ArkadePsbtExtensions.BuildIntrospectorOutput([coin]), Is.Null);
    }

    [Test]
    public void BuildIntrospectorOutput_EmitsExtensionWithCorrectVinAndScript()
    {
        var (alice, bob, introspector) = MakeKeys();
        var arkadeBytes = new byte[] { 0xc4, 0xc6 };
        var arkade = new ArkadeNofNMultisigTapScript(arkadeBytes, [alice], [introspector]);
        var plain = new NofNMultisigTapScript([alice, bob]);

        // vin 0 = plain, vin 1 = arkade — expect a single entry with vin=1.
        var coinPlain = MakeCoin(alice, bob, plain, witnessPushes: []);
        var coinArkade = MakeCoin(alice, bob, arkade, witnessPushes: [Convert.FromHexString("deadbeef")]);

        var output = ArkadePsbtExtensions.BuildIntrospectorOutput([coinPlain, coinArkade]);
        Assert.That(output, Is.Not.Null);

        // Round-trip through Extension parsing — the packet should carry one entry.
        var ext = Extension.FromScript(output!.ScriptPubKey);
        var packet = IntrospectorPacket.FromExtension(ext);
        Assert.That(packet, Is.Not.Null);
        Assert.That(packet!.Entries, Has.Count.EqualTo(1));
        Assert.That(packet.Entries[0].Vin, Is.EqualTo((ushort)1));
        Assert.That(packet.Entries[0].Script, Is.EqualTo(arkadeBytes));
        Assert.That(packet.Entries[0].Witness, Has.Count.EqualTo(1));
        Assert.That(packet.Entries[0].Witness[0], Is.EqualTo(Convert.FromHexString("deadbeef")));
    }

    [Test]
    public async Task CoSignWithIntrospectorAsync_DelegatesToProviderAndReturnsParsedPsbt()
    {
        // Build an arbitrary unsigned PSBT and an introspector mock that returns a
        // fresh PSBT base64. Caller should get back the parsed version of that base64.
        var network = Network.RegTest;
        var (alice, bob, _) = MakeKeys();
        var unsigned = BuildEmptyPsbt(network, alice, bob);
        var responsePsbt = BuildEmptyPsbt(network, alice, bob);
        var responseBase64 = responsePsbt.ToBase64();

        var introspector = Substitute.For<IIntrospectorProvider>();
        introspector
            .SubmitTxAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new IntrospectorSubmitTxResult(responseBase64, [])));

        var result = await unsigned.CoSignWithIntrospectorAsync(introspector);

        Assert.That(result.ToBase64(), Is.EqualTo(responseBase64));
        await introspector.Received(1).SubmitTxAsync(
            unsigned.ToBase64(),
            Arg.Is<IReadOnlyList<string>>(l => l.Count == 0),
            Arg.Any<CancellationToken>());
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private static (ECXOnlyPubKey alice, ECXOnlyPubKey bob, TaprootPubKey introspector) MakeKeys()
    {
        var rng = new Random(7);
        ECXOnlyPubKey Make()
        {
            var seed = new byte[32];
            rng.NextBytes(seed);
            return ECXOnlyPubKey.Create(new Key(seed).PubKey.TaprootInternalKey.ToBytes());
        }
        var introSeed = new byte[32];
        rng.NextBytes(introSeed);
        var introspector = new Key(introSeed).PubKey.GetTaprootFullPubKey().OutputKey;
        return (Make(), Make(), introspector);
    }

    private static ArkCoin MakeCoin(
        ECXOnlyPubKey alice,
        ECXOnlyPubKey bob,
        ScriptBuilder spendingBuilder,
        IReadOnlyList<byte[]>? witnessPushes = null)
    {
        var contract = new TestContract([alice, bob], spendingBuilder);
        var witnessOps = (witnessPushes ?? []).Select(Op.GetPushOp).ToArray();
        var witScript = witnessOps.Length == 0 ? null : new WitScript(witnessOps);
        var prevOut = new TxOut(Money.Coins(1), contract.GetScriptPubKey());
        var outpoint = new OutPoint(uint256.Zero, 0);
        return new ArkCoin(
            walletIdentifier: "test",
            contract: contract,
            birth: DateTimeOffset.UnixEpoch,
            expiresAt: null,
            expiresAtHeight: null,
            outPoint: outpoint,
            txOut: prevOut,
            signerDescriptor: null,
            spendingScriptBuilder: spendingBuilder,
            spendingConditionWitness: witScript,
            lockTime: null,
            sequence: null,
            swept: false,
            unrolled: false);
    }

    private static PSBT BuildEmptyPsbt(Network network, ECXOnlyPubKey a, ECXOnlyPubKey b)
    {
        var tx = Transaction.Create(network);
        tx.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        tx.Outputs.Add(new TxOut(Money.Coins(1), new Script(OpcodeType.OP_TRUE)));
        return PSBT.FromTransaction(tx, network);
    }

    private sealed class TestContract : ArkContract
    {
        private readonly ECXOnlyPubKey[] _serverKeys;
        private readonly ScriptBuilder _spending;

        public TestContract(ECXOnlyPubKey[] serverKeys, ScriptBuilder spending)
            : base(BuildServerDescriptor(serverKeys[0]))
        {
            _serverKeys = serverKeys;
            _spending = spending;
        }

        public override string Type => "test";

        protected override IEnumerable<ScriptBuilder> GetScriptBuilders() { yield return _spending; }

        protected override Dictionary<string, string> GetContractData() => new() { ["arkcontract"] = Type };

        private static OutputDescriptor BuildServerDescriptor(ECXOnlyPubKey key)
        {
            // OutputDescriptor for a fixed taproot internal key — sufficient
            // for ArkAddress generation in this test even though we don't
            // actually exercise it.
            var hex = Convert.ToHexString(key.ToBytes()).ToLowerInvariant();
            return OutputDescriptor.Parse($"rawtr({hex})", Network.RegTest);
        }
    }
}
