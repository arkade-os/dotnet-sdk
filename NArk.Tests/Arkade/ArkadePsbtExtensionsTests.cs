using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Scripts;
using NArk.Arkade.Crypto;
using NArk.Arkade.Emulator;
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
    public void RequiresEmulatorCoSigning_TrueWhenAnyArkadeBound()
    {
        var (alice, bob, emulator) = MakeKeys();
        var arkade = new ArkadeNofNMultisigTapScript([0xc4], [alice], [emulator]);
        var plain = new NofNMultisigTapScript([alice, bob]);

        var coinArkade = MakeCoin(alice, bob, arkade);
        var coinPlain = MakeCoin(alice, bob, plain);

        Assert.That(ArkadePsbtExtensions.RequiresEmulatorCoSigning([coinArkade]), Is.True);
        Assert.That(ArkadePsbtExtensions.RequiresEmulatorCoSigning([coinPlain]), Is.False);
        // Mixed: still true as long as at least one is arkade.
        Assert.That(ArkadePsbtExtensions.RequiresEmulatorCoSigning([coinPlain, coinArkade]), Is.True);
    }

    [Test]
    public void BuildEmulatorPackets_EmitsEntryWithCorrectVinAndScript()
    {
        var (alice, bob, emulator) = MakeKeys();
        var arkadeBytes = new byte[] { 0xc4, 0xc6 };
        var arkade = new ArkadeNofNMultisigTapScript(arkadeBytes, [alice], [emulator]);
        var plain = new NofNMultisigTapScript([alice, bob]);

        // vin 0 = plain, vin 1 = arkade — expect a single entry with vin=1.
        var coinPlain = MakeCoin(alice, bob, plain, witnessPushes: []);
        var coinArkade = MakeCoin(alice, bob, arkade, witnessPushes: [Convert.FromHexString("deadbeef")]);

        var packets = ArkadePsbtExtensions.BuildEmulatorPackets([coinPlain, coinArkade]);
        Assert.That(packets, Has.Count.EqualTo(1));

        var packet = packets[0] as EmulatorPacket;
        Assert.That(packet, Is.Not.Null);
        Assert.That(packet!.Entries, Has.Count.EqualTo(1));
        Assert.That(packet.Entries[0].Vin, Is.EqualTo((ushort)1));
        Assert.That(packet.Entries[0].Script, Is.EqualTo(arkadeBytes));
        Assert.That(packet.Entries[0].Witness, Has.Count.EqualTo(1));
        Assert.That(packet.Entries[0].Witness[0], Is.EqualTo(Convert.FromHexString("deadbeef")));
    }

    [Test]
    public void BuildEmulatorPackets_EmptyWhenNoArkadeCoin_OnePacketOtherwise()
    {
        var (alice, bob, emulator) = MakeKeys();
        var plain = new NofNMultisigTapScript([alice, bob]);
        var arkade = new ArkadeNofNMultisigTapScript([0xc4], [alice], [emulator]);

        Assert.That(ArkadePsbtExtensions.BuildEmulatorPackets([MakeCoin(alice, bob, plain)]), Is.Empty);

        var packets = ArkadePsbtExtensions.BuildEmulatorPackets([MakeCoin(alice, bob, arkade)]);
        Assert.That(packets, Has.Count.EqualTo(1));
        Assert.That(packets[0], Is.InstanceOf<EmulatorPacket>());
    }

    [Test]
    public void MergedExtension_CarriesBothAssetAndEmulatorPackets_InOneOpReturn()
    {
        // Mirrors SpendingService.BuildExtensionOutput: asset packet + emulator
        // packet merged into a single Extension, so both must survive in one OP_RETURN.
        var (alice, bob, emulator) = MakeKeys();
        var arkadeBytes = new byte[] { 0xc4, 0xc6 };
        var arkade = new ArkadeNofNMultisigTapScript(arkadeBytes, [alice], [emulator]);
        var coinArkade = MakeCoin(alice, bob, arkade, witnessPushes: [Convert.FromHexString("deadbeef")]);

        var emulatorPacket = ArkadePsbtExtensions.BuildEmulatorPackets([coinArkade])[0];

        var assetId = Convert.ToHexString(Enumerable.Repeat((byte)0x11, 34).ToArray()).ToLowerInvariant();
        var assetPacket = AssetPacketBuilder.BuildPacket(
            [(assetId, (ushort)0, 1000UL)], outputs: null, changeVout: 0);
        Assert.That(assetPacket, Is.Not.Null);

        var merged = new Extension([assetPacket!, emulatorPacket]).ToTxOut();

        var ext = Extension.FromScript(merged.ScriptPubKey);
        // Asset packet survives the round-trip…
        Assert.That(ext.GetAssetPacket(), Is.Not.Null);
        // …and so does the emulator packet, from the same single OP_RETURN.
        var emu = EmulatorPacket.FromExtension(ext);
        Assert.That(emu, Is.Not.Null);
        Assert.That(emu!.Entries, Has.Count.EqualTo(1));
        Assert.That(emu.Entries[0].Script, Is.EqualTo(arkadeBytes));
    }

    [Test]
    public async Task CoSignWithEmulatorAsync_DelegatesToProviderAndReturnsParsedPsbt()
    {
        // Build an arbitrary unsigned PSBT and an emulator mock that returns a
        // fresh PSBT base64. Caller should get back the parsed version of that base64.
        var network = Network.RegTest;
        var (alice, bob, _) = MakeKeys();
        var unsigned = BuildEmptyPsbt(network, alice, bob);
        var responsePsbt = BuildEmptyPsbt(network, alice, bob);
        var responseBase64 = responsePsbt.ToBase64();

        var emulator = Substitute.For<IEmulatorProvider>();
        emulator
            .SubmitTxAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EmulatorSubmitTxResult(responseBase64, [])));

        var result = await unsigned.CoSignWithEmulatorAsync(emulator);

        Assert.That(result.ToBase64(), Is.EqualTo(responseBase64));
        await emulator.Received(1).SubmitTxAsync(
            unsigned.ToBase64(),
            Arg.Is<IReadOnlyList<string>>(l => l.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public void SpendSubmitter_ShouldHandle_OnlyWhenArkadeBound()
    {
        var (alice, bob, emulator) = MakeKeys();
        var arkade = new ArkadeNofNMultisigTapScript([0xc4], [alice], [emulator]);
        var plain = new NofNMultisigTapScript([alice, bob]);
        var submitter = new ArkadeEmulatorSpendSubmitter(Substitute.For<IEmulatorProvider>());

        Assert.That(submitter.ShouldHandle([MakeCoin(alice, bob, arkade)]), Is.True);
        Assert.That(submitter.ShouldHandle([MakeCoin(alice, bob, plain)]), Is.False);
    }

    [Test]
    public async Task SpendSubmitter_SubmitAsync_ForwardsArkTxAndCheckpointsToEmulator()
    {
        var network = Network.RegTest;
        var (alice, bob, _) = MakeKeys();
        var arkTx = BuildEmptyPsbt(network, alice, bob);
        var checkpoint = BuildEmptyPsbt(network, alice, bob);

        var emulator = Substitute.For<IEmulatorProvider>();
        emulator.SubmitTxAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EmulatorSubmitTxResult(arkTx.ToBase64(), [])));

        var submitter = new ArkadeEmulatorSpendSubmitter(emulator);
        await submitter.SubmitAsync([], arkTx, [checkpoint], CancellationToken.None);

        await emulator.Received(1).SubmitTxAsync(
            arkTx.ToBase64(),
            Arg.Is<IReadOnlyList<string>>(l => l.Count == 1 && l[0] == checkpoint.ToBase64()),
            Arg.Any<CancellationToken>());
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private static (ECXOnlyPubKey alice, ECXOnlyPubKey bob, TaprootPubKey emulator) MakeKeys()
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
        var emulator = new Key(introSeed).PubKey.GetTaprootFullPubKey().OutputKey;
        return (Make(), Make(), emulator);
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
