using NArk.Abstractions;
using NArk.Abstractions.Batches;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Scripts;
using NArk.Arkade.Emulator;
using NArk.Arkade.Scripts;
using NArk.Core.Scripts;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NSubstitute;

namespace NArk.Tests.Arkade;

/// <summary>
/// Drives <see cref="ArkadeBatchSessionExtension"/> with a substituted
/// <see cref="IEmulatorProvider"/> and verifies the engagement gate plus the
/// two phases it refuses rather than submitting a request the emulator cannot
/// accept.
/// </summary>
[TestFixture]
public class ArkadeBatchSessionExtensionTests
{
    private IEmulatorProvider _emulator = null!;
    private ArkadeBatchSessionExtension _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _emulator = Substitute.For<IEmulatorProvider>();
        _sut = new ArkadeBatchSessionExtension(_emulator);
    }

    [Test]
    public async Task ShouldHandle_FalseWhenNoArkadeCoin()
    {
        var coins = new[] { MakeCoin(MakePlainBuilder()) };
        Assert.That(await _sut.ShouldHandleAsync(coins, CancellationToken.None), Is.False);
    }

    [Test]
    public async Task ShouldHandle_TrueWhenAnyArkadeCoin()
    {
        var coins = new[] { MakeCoin(MakePlainBuilder()), MakeCoin(MakeArkadeBuilder()) };
        Assert.That(await _sut.ShouldHandleAsync(coins, CancellationToken.None), Is.True);
    }

    [Test]
    public async Task CoSign_PassesThroughWhenNoArkadeCoin()
    {
        var coins = new[] { MakeCoin(MakePlainBuilder()) };
        var psbts = new[] { BuildEmptyPsbt(), BuildEmptyPsbt() };

        var signed = await _sut.CoSignAsync(
            BatchExtensionPhase.PostTreeSigning, psbts, coins, CancellationToken.None);

        Assert.That(signed, Is.SameAs(psbts), "should pass through unchanged");
        await _emulator.DidNotReceive().SubmitTxAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CoSign_RefusesTreeTransactions_RatherThanSubmittingAMalformedRequest()
    {
        // POST /v1/tx requires one checkpoint per input plus a prevarktx field on every
        // input before it inspects anything else (emulator v0.0.7,
        // internal/application/prevout.go). A tree transaction has neither, so this hop
        // reached the emulator with a request it cannot accept. Refuse at the call site,
        // where the reason is legible, and never touch the emulator.
        var coins = new[] { MakeCoin(MakeArkadeBuilder()) };
        var psbts = new[] { BuildEmptyPsbt() };

        Assert.That(
            async () => await _sut.CoSignAsync(
                BatchExtensionPhase.PostTreeSigning, psbts, coins, CancellationToken.None),
            Throws.InstanceOf<NotSupportedException>().With.Message.Contains("checkpoint"));

        await _emulator.DidNotReceive().SubmitTxAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public void CoSign_RefusesForfeits_RatherThanRoutingThemToSubmitTx()
    {
        // The emulator signs forfeits via POST /v1/finalization, which additionally needs
        // the signed intent proof, connector tree and commitment tx. POST /v1/tx does not
        // sign forfeits, so routing this phase there would return an unsigned-forfeit PSBT
        // that reads as success. Until the intent proof is threaded through from
        // registration time, refuse loudly.
        var coins = new[] { MakeCoin(MakeArkadeBuilder()) };
        var psbts = new[] { BuildEmptyPsbt() };

        Assert.That(
            async () => await _sut.CoSignAsync(
                BatchExtensionPhase.PreForfeitFinalization, psbts, coins, CancellationToken.None),
            Throws.InstanceOf<NotSupportedException>());
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private static (ECXOnlyPubKey alice, ECXOnlyPubKey bob, TaprootPubKey emulator) MakeKeys()
    {
        var rng = new Random(11);
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

    private static ScriptBuilder MakePlainBuilder()
    {
        var (alice, bob, _) = MakeKeys();
        return new NofNMultisigTapScript([alice, bob]);
    }

    private static ScriptBuilder MakeArkadeBuilder()
    {
        var (alice, _, emulator) = MakeKeys();
        return new ArkadeNofNMultisigTapScript([0xc4], [alice], [emulator]);
    }

    private static ArkCoin MakeCoin(ScriptBuilder spendingBuilder)
    {
        var (alice, bob, _) = MakeKeys();
        var contract = new TestContract([alice, bob], spendingBuilder);
        return new ArkCoin(
            walletIdentifier: "test",
            contract: contract,
            birth: DateTimeOffset.UnixEpoch,
            expiresAt: null,
            expiresAtHeight: null,
            outPoint: new OutPoint(uint256.Zero, 0),
            txOut: new TxOut(Money.Coins(1), contract.GetScriptPubKey()),
            signerDescriptor: null,
            spendingScriptBuilder: spendingBuilder,
            spendingConditionWitness: null,
            lockTime: null,
            sequence: null,
            swept: false,
            unrolled: false);
    }

    private static PSBT BuildEmptyPsbt()
    {
        var network = Network.RegTest;
        var tx = Transaction.Create(network);
        tx.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        tx.Outputs.Add(new TxOut(Money.Coins(1), new Script(OpcodeType.OP_TRUE)));
        return PSBT.FromTransaction(tx, network);
    }

    private sealed class TestContract : ArkContract
    {
        private readonly ScriptBuilder _spending;

        public TestContract(ECXOnlyPubKey[] serverKeys, ScriptBuilder spending)
            : base(BuildServerDescriptor(serverKeys[0]))
        {
            _spending = spending;
        }

        public override string Type => "test";
        public override ContractScope DefaultScope => ContractScope.Offchain;
        protected override IEnumerable<ScriptBuilder> GetScriptBuilders() { yield return _spending; }
        protected override Dictionary<string, string> GetContractData() => new() { ["arkcontract"] = Type };

        private static OutputDescriptor BuildServerDescriptor(ECXOnlyPubKey key)
        {
            var hex = Convert.ToHexString(key.ToBytes()).ToLowerInvariant();
            return OutputDescriptor.Parse($"rawtr({hex})", Network.RegTest);
        }
    }
}
