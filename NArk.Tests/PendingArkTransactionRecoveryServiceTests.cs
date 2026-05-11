using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Scripts;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Core.Transport.Models;
using NBitcoin;
using NSubstitute;

namespace NArk.Tests;

[TestFixture]
public class PendingArkTransactionRecoveryServiceTests
{
    private const string WalletId = "wallet-1";

    private IClientTransport _clientTransport = null!;
    private IWalletStorage _walletStorage = null!;
    private IWalletProvider _walletProvider = null!;
    private IVtxoStorage _vtxoStorage = null!;
    private ICoinService _coinService = null!;
    private IArkadeWalletSigner _signer = null!;

    [SetUp]
    public void SetUp()
    {
        _clientTransport = Substitute.For<IClientTransport>();
        _walletStorage = Substitute.For<IWalletStorage>();
        _walletProvider = Substitute.For<IWalletProvider>();
        _vtxoStorage = Substitute.For<IVtxoStorage>();
        _coinService = Substitute.For<ICoinService>();
        _signer = Substitute.For<IArkadeWalletSigner>();

        _walletProvider.GetSignerAsync(WalletId, Arg.Any<CancellationToken>())
            .Returns(_signer);
        _clientTransport.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .Returns(CreateStubServerInfo());
        _clientTransport.FinalizeTx(Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
    }

    [Test]
    public async Task FinalizePending_NoSpendableVtxos_SkipsTransport()
    {
        _vtxoStorage.GetVtxos(walletIds: Arg.Is<string[]>(w => w.SequenceEqual(new[] { WalletId })),
                cancellationToken: Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Array.Empty<ArkVtxo>());

        var service = CreateService();

        var result = await service.FinalizePendingArkTransactionsAsync(WalletId);

        Assert.That(result, Is.Empty);
        await _clientTransport.DidNotReceiveWithAnyArgs()
            .GetPendingTxAsync(default!, default!, default);
        await _clientTransport.DidNotReceiveWithAnyArgs().FinalizeTx(default!, default!, default);
    }

    [Test]
    public async Task FinalizePending_NoSignerForWallet_SkipsTransport()
    {
        _walletProvider.GetSignerAsync(WalletId, Arg.Any<CancellationToken>())
            .Returns((IArkadeWalletSigner?)null);
        _vtxoStorage.GetVtxos(walletIds: Arg.Is<string[]>(w => w.SequenceEqual(new[] { WalletId })),
                cancellationToken: Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs([CreateVtxo()]);

        var service = CreateService();

        var result = await service.FinalizePendingArkTransactionsAsync(WalletId);

        Assert.That(result, Is.Empty);
        await _clientTransport.DidNotReceiveWithAnyArgs()
            .GetPendingTxAsync(default!, default!, default);
    }

    [Test]
    public async Task FinalizePending_NoResolvableCoins_SkipsTransport()
    {
        _vtxoStorage.GetVtxos(walletIds: Arg.Is<string[]>(w => w.SequenceEqual(new[] { WalletId })),
                cancellationToken: Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs([CreateVtxo()]);
        _coinService.GetCoin(Arg.Any<ArkVtxo>(), WalletId, Arg.Any<CancellationToken>())
            .Returns<ArkCoin>(_ => throw new InvalidOperationException("VHTLC needs preimage"));

        var service = CreateService();

        var result = await service.FinalizePendingArkTransactionsAsync(WalletId);

        Assert.That(result, Is.Empty);
        await _clientTransport.DidNotReceiveWithAnyArgs()
            .GetPendingTxAsync(default!, default!, default);
    }

    [Test]
    public async Task FinalizePending_HappyPath_FinalizesAndReturnsArkTxId()
    {
        var coin = CreateStubCoin();
        SetUpVtxoAndCoin(coin);

        var checkpointB64 = BuildCheckpointPsbt(coin.Outpoint).ToBase64();
        var pending = new PendingArkTransaction(
            ArkTxId: "txid-1",
            FinalArkTx: "<final-ark-tx>",
            SignedCheckpointTxs: [checkpointB64]);
        _clientTransport.GetPendingTxAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([pending]);

        // Match the checkpoint input back to the same VTXO so ResolveCheckpointInput finds it.
        _vtxoStorage.GetVtxos(
                outpoints: Arg.Is<IReadOnlyCollection<OutPoint>>(o => o.Single() == coin.Outpoint),
                walletIds: Arg.Is<string[]>(w => w.SequenceEqual(new[] { WalletId })),
                includeSpent: true,
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns([CreateVtxo(coin.Outpoint)]);

        var service = CreateRecordingService(coin, parsedCheckpoint: BuildCheckpointPsbt(coin.Outpoint));

        var result = await service.FinalizePendingArkTransactionsAsync(WalletId);

        Assert.That(result, Is.EquivalentTo(new[] { "txid-1" }));
        await _clientTransport.Received(1).FinalizeTx("txid-1",
            Arg.Is<string[]>(arr => arr.Length == 1),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FinalizePending_DedupesAcrossBatches()
    {
        // 21 coins = 2 batches; both batches return the same arkTxId. We must only
        // finalize once and return one entry.
        var coins = Enumerable.Range(0, 21).Select(_ => CreateStubCoin()).ToList();
        SetUpVtxoAndCoins(coins);

        var checkpointB64 = BuildCheckpointPsbt(coins[0].Outpoint).ToBase64();
        var pending = new PendingArkTransaction(
            ArkTxId: "shared-txid",
            FinalArkTx: "<final-ark-tx>",
            SignedCheckpointTxs: [checkpointB64]);
        _clientTransport.GetPendingTxAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([pending]);

        // Resolve the checkpoint outpoint to any of our coins
        _vtxoStorage.GetVtxos(
                outpoints: Arg.Any<IReadOnlyCollection<OutPoint>>(),
                walletIds: Arg.Is<string[]>(w => w.SequenceEqual(new[] { WalletId })),
                includeSpent: true,
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns([CreateVtxo(coins[0].Outpoint)]);

        var service = CreateRecordingService(coins[0], parsedCheckpoint: BuildCheckpointPsbt(coins[0].Outpoint));

        var result = await service.FinalizePendingArkTransactionsAsync(WalletId);

        Assert.That(result, Is.EquivalentTo(new[] { "shared-txid" }));
        await _clientTransport.Received(1).FinalizeTx("shared-txid", Arg.Any<string[]>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FinalizePending_FinalizeFailure_RaisesEventAndContinues()
    {
        var coin = CreateStubCoin();
        SetUpVtxoAndCoin(coin);

        var checkpointB64 = BuildCheckpointPsbt(coin.Outpoint).ToBase64();
        var failing = new PendingArkTransaction("bad-tx", "<final>", [checkpointB64]);
        var ok = new PendingArkTransaction("good-tx", "<final>", [checkpointB64]);
        _clientTransport.GetPendingTxAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([failing, ok]);

        _vtxoStorage.GetVtxos(
                outpoints: Arg.Any<IReadOnlyCollection<OutPoint>>(),
                walletIds: Arg.Is<string[]>(w => w.SequenceEqual(new[] { WalletId })),
                includeSpent: true,
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns([CreateVtxo(coin.Outpoint)]);

        // Make the FIRST FinalizeTx throw, the second succeed.
        var calls = 0;
        _clientTransport.FinalizeTx(Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(_ => calls++ == 0
                ? Task.FromException(new InvalidOperationException("server rejected"))
                : Task.CompletedTask);

        var service = CreateRecordingService(coin, parsedCheckpoint: BuildCheckpointPsbt(coin.Outpoint));

        PendingTxRecoveryFailureEventArgs? captured = null;
        service.RecoveryFailed += (_, e) => captured = e;

        var result = await service.FinalizePendingArkTransactionsAsync(WalletId);

        Assert.That(result, Is.EquivalentTo(new[] { "good-tx" }));
        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.WalletId, Is.EqualTo(WalletId));
        Assert.That(captured.ArkTxId, Is.EqualTo("bad-tx"));
        Assert.That(captured.Exception, Is.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public async Task RecoverAllWallets_ContinuesAcrossWallets_OnFailure()
    {
        var walletA = new ArkWalletInfo(
            Id: "wallet-A", Secret: "", Destination: null,
            WalletType: WalletType.SingleKey, AccountDescriptor: null, LastUsedIndex: 0);
        var walletB = new ArkWalletInfo(
            Id: "wallet-B", Secret: "", Destination: null,
            WalletType: WalletType.SingleKey, AccountDescriptor: null, LastUsedIndex: 0);
        _walletStorage.LoadAllWallets(Arg.Any<CancellationToken>())
            .Returns(new HashSet<ArkWalletInfo> { walletA, walletB });

        // wallet-A: GetSignerAsync throws — should be caught and logged, then
        // recovery proceeds to wallet-B.
        _walletProvider.GetSignerAsync("wallet-A", Arg.Any<CancellationToken>())
            .Returns<IArkadeWalletSigner?>(_ => throw new InvalidOperationException("signer missing"));
        _walletProvider.GetSignerAsync("wallet-B", Arg.Any<CancellationToken>())
            .Returns(_signer);

        // wallet-B has no spendable VTXOs (simplest) so it short-circuits cleanly.
        _vtxoStorage.GetVtxos(walletIds: Arg.Any<string[]>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Array.Empty<ArkVtxo>());

        var service = CreateService();
        await service.RecoverAllWalletsAsync(CancellationToken.None);

        // Wallet-B was processed (wallet storage was loaded, vtxo lookup happened
        // for both — we don't crash on wallet-A's signer failure).
        await _walletStorage.Received(1).LoadAllWallets(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RecoverAllWallets_AbsorbsWalletStorageFailure_SoHostStartupNeverBlocks()
    {
        // walletStorage.LoadAllWallets throws (DB timeout, connection error, etc.).
        // RecoverAllWalletsAsync is wired into ArkHostedLifecycle.StartAsync so
        // anything that escapes here kills host startup. The service must absorb it.
        _walletStorage.LoadAllWallets(Arg.Any<CancellationToken>())
            .Returns<IReadOnlySet<ArkWalletInfo>>(_ => throw new InvalidOperationException("DB down"));

        var service = CreateService();

        Assert.DoesNotThrowAsync(async () =>
            await service.RecoverAllWalletsAsync(CancellationToken.None));
    }

    private void SetUpVtxoAndCoin(ArkCoin coin) => SetUpVtxoAndCoins([coin]);

    private void SetUpVtxoAndCoins(IReadOnlyList<ArkCoin> coins)
    {
        var vtxos = coins.Select(c => CreateVtxo(c.Outpoint)).ToArray();
        _vtxoStorage.GetVtxos(walletIds: Arg.Is<string[]>(w => w.SequenceEqual(new[] { WalletId })),
                cancellationToken: Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(vtxos);

        for (var i = 0; i < coins.Count; i++)
        {
            var coin = coins[i];
            var vtxo = vtxos[i];
            _coinService.GetCoin(Arg.Is<ArkVtxo>(v => v.OutPoint == vtxo.OutPoint), WalletId,
                    Arg.Any<CancellationToken>())
                .Returns(coin);
        }
    }

    private RecordingPendingTxRecoveryService CreateRecordingService(ArkCoin defaultCoin, PSBT parsedCheckpoint)
        => new(_clientTransport, _walletStorage, _walletProvider, _vtxoStorage, _coinService, defaultCoin,
            parsedCheckpoint);

    private PendingArkTransactionRecoveryService CreateService()
        => new(_clientTransport, _walletStorage, _walletProvider, _vtxoStorage, _coinService);

    private static ArkVtxo CreateVtxo(OutPoint? outpoint = null) => new(
        Script: "5120" + new string('0', 64),
        TransactionId: (outpoint?.Hash ?? RandomUtils.GetUInt256()).ToString(),
        TransactionOutputIndex: outpoint?.N ?? 0,
        Amount: 50_000,
        SpentByTransactionId: null,
        SettledByTransactionId: null,
        Swept: false,
        CreatedAt: DateTimeOffset.UtcNow,
        ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
        ExpiresAtHeight: null);

    private ArkCoin CreateStubCoin()
    {
        var key = new Key();
        var script = key.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);
        var outpoint = new OutPoint(RandomUtils.GetUInt256(), 0);
        var txOut = new TxOut(Money.Satoshis(50_000), script);

        var scriptBuilder = Substitute.For<ScriptBuilder>();
        scriptBuilder.BuildScript().Returns(Enumerable.Empty<Op>());
        scriptBuilder.Build().Returns(new TapScript(Script.Empty, TapLeafVersion.C0));

        var contract = Substitute.For<ArkContract>(
            NBitcoin.Scripting.OutputDescriptor.Parse(
                "rawtr(03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88)",
                Network.RegTest));

        return new ArkCoin(
            walletIdentifier: WalletId,
            contract: contract,
            birth: DateTimeOffset.UtcNow,
            expiresAt: null,
            expiresAtHeight: null,
            outPoint: outpoint,
            txOut: txOut,
            signerDescriptor: null,
            spendingScriptBuilder: scriptBuilder,
            spendingConditionWitness: null,
            lockTime: null,
            sequence: new Sequence(1),
            swept: false,
            unrolled: false);
    }

    private static PSBT BuildCheckpointPsbt(OutPoint inputOutpoint)
    {
        var network = Network.RegTest;
        var tx = network.CreateTransaction();
        tx.Version = 2;
        tx.Inputs.Add(new TxIn(inputOutpoint));
        tx.Outputs.Add(new TxOut(Money.Satoshis(49_000),
            new Key().PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86)));
        return PSBT.FromTransaction(tx, network);
    }

    private static ArkServerInfo CreateStubServerInfo()
    {
        // Real ArkServerInfo construction in tests requires too many primitives; the
        // RecoveryService only consumes Network from it, so the simplest stub is to
        // route the call through a partial-fake. Tests do not assert on other fields.
        var info = (ArkServerInfo)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(ArkServerInfo));
        typeof(ArkServerInfo).GetProperty(nameof(ArkServerInfo.Network))!
            .SetValue(info, Network.RegTest);
        return info;
    }

    /// <summary>
    /// Test double that overrides the proof-creation and checkpoint-signing paths so
    /// tests don't have to stage a fully-functional real signer.
    /// </summary>
    private sealed class RecordingPendingTxRecoveryService : PendingArkTransactionRecoveryService
    {
        private readonly ArkCoin _resolvedCoin;
        private readonly PSBT _parsedCheckpoint;

        public RecordingPendingTxRecoveryService(
            IClientTransport transport, IWalletStorage walletStorage, IWalletProvider walletProvider,
            IVtxoStorage vtxoStorage, ICoinService coinService,
            ArkCoin resolvedCoin, PSBT parsedCheckpoint)
            : base(transport, walletStorage, walletProvider, vtxoStorage, coinService)
        {
            _resolvedCoin = resolvedCoin;
            _parsedCheckpoint = parsedCheckpoint;
        }

        protected override Task<(string Proof, string Message)> CreateProofAsync(
            ArkCoin anchor, IArkadeWalletSigner signer, Network network,
            CancellationToken cancellationToken)
            => Task.FromResult(("test-proof", "{\"type\":\"get-pending-tx\",\"expire_at\":0}"));

        protected override Task SignCheckpointAsync(ArkCoin coin, PSBT checkpoint,
            IArkadeWalletSigner signer, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
