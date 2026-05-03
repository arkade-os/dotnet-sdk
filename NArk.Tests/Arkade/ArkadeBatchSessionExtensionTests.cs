using NArk.Abstractions;
using NArk.Abstractions.Batches;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Scripts;
using NArk.Arkade.Introspector;
using NArk.Arkade.Scripts;
using NArk.Core.Scripts;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NSubstitute;

namespace NArk.Tests.Arkade;

/// <summary>
/// Drives <see cref="ArkadeBatchSessionExtension"/> with a substituted
/// <see cref="IIntrospectorProvider"/> and verifies the engagement gate +
/// the per-PSBT co-signing routing.
/// </summary>
[TestFixture]
public class ArkadeBatchSessionExtensionTests
{
    private IIntrospectorProvider _introspector = null!;
    private ArkadeBatchSessionExtension _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _introspector = Substitute.For<IIntrospectorProvider>();
        _sut = new ArkadeBatchSessionExtension(_introspector);
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
        await _introspector.DidNotReceive().SubmitTxAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [TestCase(BatchExtensionPhase.PostTreeSigning)]
    [TestCase(BatchExtensionPhase.PreForfeitFinalization)]
    public async Task CoSign_DispatchesEachPsbtToIntrospector(BatchExtensionPhase phase)
    {
        var coins = new[] { MakeCoin(MakeArkadeBuilder()) };
        var psbts = new[] { BuildEmptyPsbt(), BuildEmptyPsbt(), BuildEmptyPsbt() };

        // Provider returns a fresh PSBT base64 each call so we can assert the
        // result isn't the input collection by reference.
        _introspector
            .SubmitTxAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(
                new IntrospectorSubmitTxResult(BuildEmptyPsbt().ToBase64(), [])));

        var signed = await _sut.CoSignAsync(phase, psbts, coins, CancellationToken.None);

        Assert.That(signed, Has.Count.EqualTo(psbts.Length));
        await _introspector.Received(psbts.Length).SubmitTxAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CoSign_PropagatesIntrospectorFailure()
    {
        var coins = new[] { MakeCoin(MakeArkadeBuilder()) };
        var psbts = new[] { BuildEmptyPsbt() };

        _introspector
            .SubmitTxAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns<Task<IntrospectorSubmitTxResult>>(_ =>
                throw new HttpRequestException("introspector down"));

        var ex = Assert.ThrowsAsync<HttpRequestException>(async () =>
            await _sut.CoSignAsync(
                BatchExtensionPhase.PostTreeSigning, psbts, coins, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("introspector down"));
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private static (ECXOnlyPubKey alice, ECXOnlyPubKey bob, TaprootPubKey introspector) MakeKeys()
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
        var introspector = new Key(introSeed).PubKey.GetTaprootFullPubKey().OutputKey;
        return (Make(), Make(), introspector);
    }

    private static ScriptBuilder MakePlainBuilder()
    {
        var (alice, bob, _) = MakeKeys();
        return new NofNMultisigTapScript([alice, bob]);
    }

    private static ScriptBuilder MakeArkadeBuilder()
    {
        var (alice, _, introspector) = MakeKeys();
        return new ArkadeNofNMultisigTapScript([0xc4], [alice], [introspector]);
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
        protected override IEnumerable<ScriptBuilder> GetScriptBuilders() { yield return _spending; }
        protected override Dictionary<string, string> GetContractData() => new() { ["arkcontract"] = Type };

        private static OutputDescriptor BuildServerDescriptor(ECXOnlyPubKey key)
        {
            var hex = Convert.ToHexString(key.ToBytes()).ToLowerInvariant();
            return OutputDescriptor.Parse($"rawtr({hex})", Network.RegTest);
        }
    }
}
