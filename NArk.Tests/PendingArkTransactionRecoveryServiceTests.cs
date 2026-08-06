using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Helpers;
using NArk.Abstractions.Scripts;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core;
using NArk.Core.Contracts;
using NArk.Core.Scripts;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Core.Transport.Models;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NSubstitute;

namespace NArk.Tests;

[TestFixture]
public class PendingArkTransactionRecoveryServiceTests
{
    private const string WalletId = "wallet-1";
    private static readonly Network Net = Network.RegTest;

    /// <summary>Arkade P2A anchor marker (mirrors the internal <c>NArk.Core.Constants.ArkP2A</c>).</summary>
    private static readonly Script P2A = Script.FromHex("51024e73");

    private IClientTransport _clientTransport = null!;
    private IWalletStorage _walletStorage = null!;
    private IWalletProvider _walletProvider = null!;
    private IVtxoStorage _vtxoStorage = null!;
    private ICoinService _coinService = null!;
    private IArkadeWalletSigner _signer = null!;
    private ArkServerInfo _serverInfo = null!;

    [SetUp]
    public void SetUp()
    {
        _clientTransport = Substitute.For<IClientTransport>();
        _walletStorage = Substitute.For<IWalletStorage>();
        _walletProvider = Substitute.For<IWalletProvider>();
        _vtxoStorage = Substitute.For<IVtxoStorage>();
        _coinService = Substitute.For<ICoinService>();
        _signer = Substitute.For<IArkadeWalletSigner>();
        _serverInfo = CreateStubServerInfo();

        _walletProvider.GetSignerAsync(WalletId, Arg.Any<CancellationToken>())
            .Returns(_signer);
        _clientTransport.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .Returns(_serverInfo);
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
        var wallet = CreateWalletCoin();
        SetUpVtxoAndCoin(wallet);

        var pending = wallet.BuildPendingTx();
        StubPendingTxs(pending);

        var service = CreateRecordingService();

        var result = await service.FinalizePendingArkTransactionsAsync(WalletId);

        Assert.That(result, Is.EquivalentTo(new[] { pending.ArkTxId }));
        Assert.That(service.SignedCheckpoints, Is.EqualTo(1));
        await _clientTransport.Received(1).FinalizeTx(pending.ArkTxId,
            Arg.Is<string[]>(arr => arr.Length == 1),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FinalizePending_CheckpointPaysForeignScript_IsRejectedUnsigned()
    {
        // A checkpoint that moves the input somewhere other than this wallet's checkpoint
        // contract. The signature would commit to that output (SIGHASH_DEFAULT), and the
        // server holds the other half of the 2-of-2, so it is never produced.
        var wallet = CreateWalletCoin();
        SetUpVtxoAndCoin(wallet);

        var foreignScript = new Key().PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);
        var pending = wallet.BuildPendingTx(checkpointDestination: foreignScript);
        StubPendingTxs(pending);

        var service = CreateRecordingService();
        var failures = new List<PendingTxRecoveryFailureEventArgs>();
        service.RecoveryFailed += (_, e) => failures.Add(e);

        var result = await service.FinalizePendingArkTransactionsAsync(WalletId);

        Assert.That(result, Is.Empty);
        Assert.That(service.SignedCheckpoints, Is.Zero, "no signature may be produced for a rejected checkpoint");
        Assert.That(failures.Single().Exception, Is.InstanceOf<UnauthorizedPendingArkTransactionException>()
            .With.Property("Reason").Contains("instead of this wallet's checkpoint contract"));
        await _clientTransport.DidNotReceiveWithAnyArgs().FinalizeTx(default!, default!, default);
    }

    [Test]
    public async Task FinalizePending_CheckpointShortchangesTheInput_IsRejectedUnsigned()
    {
        var wallet = CreateWalletCoin();
        SetUpVtxoAndCoin(wallet);

        // Right destination, wrong value — the difference would be skimmed elsewhere.
        var pending = wallet.BuildPendingTx(checkpointAmount: Money.Satoshis(1_000));
        StubPendingTxs(pending);

        var service = CreateRecordingService();
        var failures = new List<PendingTxRecoveryFailureEventArgs>();
        service.RecoveryFailed += (_, e) => failures.Add(e);

        var result = await service.FinalizePendingArkTransactionsAsync(WalletId);

        Assert.That(result, Is.Empty);
        Assert.That(service.SignedCheckpoints, Is.Zero);
        Assert.That(failures.Single().Exception, Is.InstanceOf<UnauthorizedPendingArkTransactionException>()
            .With.Property("Reason").Contains("instead of the input's full"));
    }

    [Test]
    public async Task FinalizePending_ExtraCheckpointOutput_IsRejectedUnsigned()
    {
        var wallet = CreateWalletCoin();
        SetUpVtxoAndCoin(wallet);

        var pending = wallet.BuildPendingTx(extraCheckpointOutput: new TxOut(Money.Satoshis(5_000),
            new Key().PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86)));
        StubPendingTxs(pending);

        var service = CreateRecordingService();
        var failures = new List<PendingTxRecoveryFailureEventArgs>();
        service.RecoveryFailed += (_, e) => failures.Add(e);

        var result = await service.FinalizePendingArkTransactionsAsync(WalletId);

        Assert.That(result, Is.Empty);
        Assert.That(service.SignedCheckpoints, Is.Zero);
        Assert.That(failures.Single().Exception, Is.InstanceOf<UnauthorizedPendingArkTransactionException>()
            .With.Property("Reason").Contains("expected exactly 2"));
    }

    [Test]
    public async Task FinalizePending_CheckpointWithExtraInput_IsRejectedUnsigned()
    {
        // arkd builds one checkpoint per spent VTXO, so a multi-input checkpoint is not a
        // shape this wallet ever asked for: the extra input is unaccounted for and the
        // signature would commit to spending it alongside ours.
        var wallet = CreateWalletCoin();
        SetUpVtxoAndCoin(wallet);

        var pending = wallet.BuildPendingTx(extraCheckpointInput: true);
        StubPendingTxs(pending);

        var service = CreateRecordingService();
        var failures = new List<PendingTxRecoveryFailureEventArgs>();
        service.RecoveryFailed += (_, e) => failures.Add(e);

        var result = await service.FinalizePendingArkTransactionsAsync(WalletId);

        Assert.That(result, Is.Empty);
        Assert.That(service.SignedCheckpoints, Is.Zero);
        Assert.That(failures.Single().Exception, Is.InstanceOf<UnauthorizedPendingArkTransactionException>()
            .With.Property("Reason").Contains("expected exactly 1"));
        await _clientTransport.DidNotReceiveWithAnyArgs().FinalizeTx(default!, default!, default);
    }

    [Test]
    public async Task FinalizePending_CoinWithNoServerKey_FailsAsLocalStateNotAsUnauthorized()
    {
        // No server key on the contract means no expected checkpoint output can be rebuilt.
        // That is a local-state problem — the server is not implicated — so it must NOT be
        // reported as an authorization failure, which consumers treat as an attack signal.
        var wallet = CreateWalletCoin(withServerKey: false);
        SetUpVtxoAndCoin(wallet);

        StubPendingTxs(wallet.BuildPendingTx());

        var service = CreateRecordingService();
        var failures = new List<PendingTxRecoveryFailureEventArgs>();
        service.RecoveryFailed += (_, e) => failures.Add(e);

        var result = await service.FinalizePendingArkTransactionsAsync(WalletId);

        Assert.That(result, Is.Empty);
        Assert.That(service.SignedCheckpoints, Is.Zero);
        Assert.That(failures.Single().Exception, Is.InstanceOf<InvalidOperationException>());
        Assert.That(failures.Single().Exception,
            Is.Not.InstanceOf<UnauthorizedPendingArkTransactionException>(),
            "a contract with no server key is local state, not an unauthorized server response");
        await _clientTransport.DidNotReceiveWithAnyArgs().FinalizeTx(default!, default!, default);
    }

    [Test]
    public async Task FinalizePending_CovenantCoinWithNoWalletKey_SkipsSignatureCheckAndFinalizes()
    {
        // Covenant leaves (an emulator-cosigned HTLC claim and friends) name no wallet key, so
        // the ark tx carries no wallet signature to verify. The checkpoint is still validated in
        // full; the pending tx must go through rather than be rejected for a missing signature.
        var wallet = CreateWalletCoin(withWalletKey: false);
        SetUpVtxoAndCoin(wallet);

        var pending = wallet.BuildPendingTx(signArkTx: false);
        StubPendingTxs(pending);

        var service = CreateRecordingService();
        var failures = new List<PendingTxRecoveryFailureEventArgs>();
        service.RecoveryFailed += (_, e) => failures.Add(e);

        var result = await service.FinalizePendingArkTransactionsAsync(WalletId);

        Assert.That(failures, Is.Empty);
        Assert.That(result, Is.EquivalentTo(new[] { pending.ArkTxId }));
        Assert.That(service.SignedCheckpoints, Is.EqualTo(1));
        await _clientTransport.Received(1).FinalizeTx(pending.ArkTxId,
            Arg.Is<string[]>(arr => arr.Length == 1),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FinalizePending_CovenantCoinWithForeignCheckpoint_IsStillRejected()
    {
        // The covenant skip is scoped to the ark-tx signature check only — the checkpoint
        // itself is still rebuilt and compared, so a covenant coin is not a bypass.
        var wallet = CreateWalletCoin(withWalletKey: false);
        SetUpVtxoAndCoin(wallet);

        StubPendingTxs(wallet.BuildPendingTx(signArkTx: false, checkpointDestination: NewTaprootScript()));

        var service = CreateRecordingService();
        var failures = new List<PendingTxRecoveryFailureEventArgs>();
        service.RecoveryFailed += (_, e) => failures.Add(e);

        var result = await service.FinalizePendingArkTransactionsAsync(WalletId);

        Assert.That(result, Is.Empty);
        Assert.That(service.SignedCheckpoints, Is.Zero);
        Assert.That(failures.Single().Exception, Is.InstanceOf<UnauthorizedPendingArkTransactionException>()
            .With.Property("Reason").Contains("instead of this wallet's checkpoint contract"));
    }

    [Test]
    public async Task FinalizePending_ArkTxNotSignedByWallet_IsRejectedUnsigned()
    {
        // Checkpoint shape is correct, but the wallet never authorised the onward spend.
        // Signing anyway would let the server park the funds in the checkpoint contract
        // and take them via the server-only unroll path once its timeout elapses.
        var wallet = CreateWalletCoin();
        SetUpVtxoAndCoin(wallet);

        var pending = wallet.BuildPendingTx(signArkTx: false);
        StubPendingTxs(pending);

        var service = CreateRecordingService();
        var failures = new List<PendingTxRecoveryFailureEventArgs>();
        service.RecoveryFailed += (_, e) => failures.Add(e);

        var result = await service.FinalizePendingArkTransactionsAsync(WalletId);

        Assert.That(result, Is.Empty);
        Assert.That(service.SignedCheckpoints, Is.Zero);
        Assert.That(failures.Single().Exception, Is.InstanceOf<UnauthorizedPendingArkTransactionException>()
            .With.Property("Reason").Contains("carries no wallet signature"));
    }

    [Test]
    public async Task FinalizePending_ArkTxOutputsSwappedUnderTheSignature_IsRejectedUnsigned()
    {
        // Signature lifted from a genuine ark tx onto one with a different payout: it is
        // present and well-formed, but it does not verify over the transaction presented.
        var wallet = CreateWalletCoin();
        SetUpVtxoAndCoin(wallet);

        var pending = wallet.BuildPendingTx(replaceArkTxDestinationAfterSigning: true);
        StubPendingTxs(pending);

        var service = CreateRecordingService();
        var failures = new List<PendingTxRecoveryFailureEventArgs>();
        service.RecoveryFailed += (_, e) => failures.Add(e);

        var result = await service.FinalizePendingArkTransactionsAsync(WalletId);

        Assert.That(result, Is.Empty);
        Assert.That(service.SignedCheckpoints, Is.Zero);
        Assert.That(failures.Single().Exception, Is.InstanceOf<UnauthorizedPendingArkTransactionException>()
            .With.Property("Reason").Contains("does not verify"));
    }

    [Test]
    public async Task FinalizePending_ArkTxIdDoesNotMatchFinalArkTx_IsRejectedUnsigned()
    {
        var wallet = CreateWalletCoin();
        SetUpVtxoAndCoin(wallet);

        var genuine = wallet.BuildPendingTx();
        var pending = genuine with { ArkTxId = RandomUtils.GetUInt256().ToString() };
        StubPendingTxs(pending);

        var service = CreateRecordingService();
        var failures = new List<PendingTxRecoveryFailureEventArgs>();
        service.RecoveryFailed += (_, e) => failures.Add(e);

        var result = await service.FinalizePendingArkTransactionsAsync(WalletId);

        Assert.That(result, Is.Empty);
        Assert.That(service.SignedCheckpoints, Is.Zero);
        Assert.That(failures.Single().Exception, Is.InstanceOf<UnauthorizedPendingArkTransactionException>()
            .With.Property("Reason").Contains("not the advertised id"));
    }

    [Test]
    public async Task FinalizePending_ArkTxSpendsSomethingElse_IsRejectedUnsigned()
    {
        var wallet = CreateWalletCoin();
        SetUpVtxoAndCoin(wallet);

        var pending = wallet.BuildPendingTx(arkTxSpendsForeignOutpoint: true);
        StubPendingTxs(pending);

        var service = CreateRecordingService();
        var failures = new List<PendingTxRecoveryFailureEventArgs>();
        service.RecoveryFailed += (_, e) => failures.Add(e);

        var result = await service.FinalizePendingArkTransactionsAsync(WalletId);

        Assert.That(result, Is.Empty);
        Assert.That(service.SignedCheckpoints, Is.Zero);
        Assert.That(failures.Single().Exception, Is.InstanceOf<UnauthorizedPendingArkTransactionException>()
            .With.Property("Reason").Contains("not one of the checkpoint outputs"));
    }

    [Test]
    public async Task FinalizePending_DedupesAcrossBatches()
    {
        // 21 coins = 2 batches; both batches return the same arkTxId. We must only
        // finalize once and return one entry.
        var wallets = Enumerable.Range(0, 21).Select(_ => CreateWalletCoin()).ToList();
        SetUpVtxoAndCoins(wallets);

        var pending = wallets[0].BuildPendingTx();
        StubPendingTxs(pending);

        var service = CreateRecordingService();

        var result = await service.FinalizePendingArkTransactionsAsync(WalletId);

        Assert.That(result, Is.EquivalentTo(new[] { pending.ArkTxId }));
        await _clientTransport.Received(1).FinalizeTx(pending.ArkTxId, Arg.Any<string[]>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FinalizePending_FinalizeFailure_RaisesEventAndContinues()
    {
        var wallet = CreateWalletCoin();
        SetUpVtxoAndCoin(wallet);

        var failing = wallet.BuildPendingTx(arkTxDestination: NewTaprootScript());
        var ok = wallet.BuildPendingTx(arkTxDestination: NewTaprootScript());
        StubPendingTxs(failing, ok);

        // Make the FIRST FinalizeTx throw, the second succeed.
        var calls = 0;
        _clientTransport.FinalizeTx(Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(_ => calls++ == 0
                ? Task.FromException(new InvalidOperationException("server rejected"))
                : Task.CompletedTask);

        var service = CreateRecordingService();

        PendingTxRecoveryFailureEventArgs? captured = null;
        service.RecoveryFailed += (_, e) => captured = e;

        var result = await service.FinalizePendingArkTransactionsAsync(WalletId);

        Assert.That(result, Is.EquivalentTo(new[] { ok.ArkTxId }));
        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.WalletId, Is.EqualTo(WalletId));
        Assert.That(captured.ArkTxId, Is.EqualTo(failing.ArkTxId));
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

    private void StubPendingTxs(params PendingArkTransaction[] pendingTxs) =>
        _clientTransport.GetPendingTxAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(pendingTxs);

    private void SetUpVtxoAndCoin(WalletCoin wallet) => SetUpVtxoAndCoins([wallet]);

    private void SetUpVtxoAndCoins(IReadOnlyList<WalletCoin> wallets)
    {
        var vtxos = wallets.Select(w => CreateVtxo(w.Coin.Outpoint)).ToArray();
        _vtxoStorage.GetVtxos(walletIds: Arg.Is<string[]>(w => w.SequenceEqual(new[] { WalletId })),
                cancellationToken: Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(vtxos);

        // Checkpoint inputs are resolved by outpoint — route each one back to its coin.
        foreach (var wallet in wallets)
        {
            var outpoint = wallet.Coin.Outpoint;
            _vtxoStorage.GetVtxos(
                    outpoints: Arg.Is<IReadOnlyCollection<OutPoint>>(o => o.Count == 1 && o.Single() == outpoint),
                    walletIds: Arg.Is<string[]>(w => w.SequenceEqual(new[] { WalletId })),
                    includeSpent: true,
                    cancellationToken: Arg.Any<CancellationToken>())
                .Returns([CreateVtxo(outpoint)]);
        }

        for (var i = 0; i < wallets.Count; i++)
        {
            var coin = wallets[i].Coin;
            var vtxo = vtxos[i];
            _coinService.GetCoin(Arg.Is<ArkVtxo>(v => v.OutPoint == vtxo.OutPoint), WalletId,
                    Arg.Any<CancellationToken>())
                .Returns(coin);
        }
    }

    private RecordingPendingTxRecoveryService CreateRecordingService()
        => new(_clientTransport, _walletStorage, _walletProvider, _vtxoStorage, _coinService);

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

    private static Script NewTaprootScript() =>
        new Key().PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);

    private static OutputDescriptor DescriptorFor(ECPrivKey key) =>
        KeyExtensions.ParseOutputDescriptor(
            Convert.ToHexString(key.CreatePubKey().ToBytes()).ToLowerInvariant(), Net);

    /// <summary>
    /// A real payment-contract VTXO owned by a real key, so checkpoint contracts and ark tx
    /// signatures can be produced exactly the way the SDK produces them on the spending path.
    /// </summary>
    /// <param name="withWalletKey">
    /// <c>false</c> models a covenant coin (e.g. an emulator-cosigned HTLC claim): the spending
    /// leaf names no wallet key, so the wallet never signs the ark tx for it.
    /// </param>
    /// <param name="withServerKey">
    /// <c>false</c> models a coin whose contract carries no server key, so no expected checkpoint
    /// output can be reconstructed for it at all.
    /// </param>
    private WalletCoin CreateWalletCoin(bool withWalletKey = true, bool withServerKey = true)
    {
        var userKey = ECPrivKey.Create(RandomUtils.GetBytes(32));
        var userDescriptor = DescriptorFor(userKey);

        ArkContract contract;
        ScriptBuilder spendingScriptBuilder;
        if (withServerKey)
        {
            var payment = new ArkPaymentContract(_serverInfo.SignerKey, new Sequence(144), userDescriptor);
            contract = payment;
            spendingScriptBuilder = payment.CollaborativePath();
        }
        else
        {
            spendingScriptBuilder = new NofNMultisigTapScript([userDescriptor.ToXOnlyPubKey()]);
            contract = new GenericArkContract(null!, [spendingScriptBuilder]);
        }

        var outpoint = new OutPoint(RandomUtils.GetUInt256(), 0);
        var txOut = new TxOut(Money.Satoshis(50_000), contract.GetScriptPubKey());

        var coin = new ArkCoin(
            walletIdentifier: WalletId,
            contract: contract,
            birth: DateTimeOffset.UtcNow,
            expiresAt: null,
            expiresAtHeight: null,
            outPoint: outpoint,
            txOut: txOut,
            signerDescriptor: withWalletKey ? userDescriptor : null,
            spendingScriptBuilder: spendingScriptBuilder,
            spendingConditionWitness: null,
            lockTime: null,
            sequence: null,
            swept: false,
            unrolled: false);

        return new WalletCoin(coin, userKey, _serverInfo);
    }

    /// <summary>
    /// Builds the checkpoint + final ark tx pair the Arkade server hands back for a pending
    /// transaction, with knobs for each way the returned pair can deviate from what this
    /// wallet would have built.
    /// </summary>
    private sealed record WalletCoin(ArkCoin Coin, ECPrivKey UserKey, ArkServerInfo ServerInfo)
    {
        public PendingArkTransaction BuildPendingTx(
            Script? checkpointDestination = null,
            Money? checkpointAmount = null,
            TxOut? extraCheckpointOutput = null,
            bool extraCheckpointInput = false,
            Script? arkTxDestination = null,
            bool signArkTx = true,
            bool replaceArkTxDestinationAfterSigning = false,
            bool arkTxSpendsForeignOutpoint = false)
        {
            // Server key falls back to the server's own so a serverless-contract coin still
            // produces a well-formed checkpoint — validation rejects it before the outputs
            // are ever looked at.
            var checkpointContract = new GenericArkContract(Coin.Contract.Server ?? ServerInfo.SignerKey,
                [Coin.SpendingScriptBuilder, ServerInfo.CheckpointTapScript]);

            var checkpointTx = Net.CreateTransaction();
            checkpointTx.Version = 3;
            checkpointTx.Inputs.Add(new TxIn(Coin.Outpoint));
            if (extraCheckpointInput)
                checkpointTx.Inputs.Add(new TxIn(new OutPoint(RandomUtils.GetUInt256(), 0)));
            checkpointTx.Outputs.Add(new TxOut(checkpointAmount ?? Coin.Amount,
                checkpointDestination ?? checkpointContract.GetScriptPubKey()));
            checkpointTx.Outputs.Add(new TxOut(Money.Zero, P2A));
            if (extraCheckpointOutput is not null)
                checkpointTx.Outputs.Add(extraCheckpointOutput);
            var checkpoint = PSBT.FromTransaction(checkpointTx, Net);

            var checkpointOutpoint = arkTxSpendsForeignOutpoint
                ? new OutPoint(RandomUtils.GetUInt256(), 0)
                : new OutPoint(checkpointTx, 0);
            var checkpointTxOut = checkpointTx.Outputs[0];

            var arkTx = Net.CreateTransaction();
            arkTx.Version = 3;
            arkTx.Inputs.Add(new TxIn(checkpointOutpoint));
            arkTx.Outputs.Add(new TxOut(checkpointTxOut.Value, arkTxDestination ?? NewTaprootScript()));
            arkTx.Outputs.Add(new TxOut(Money.Zero, P2A));

            var arkPsbt = PSBT.FromTransaction(arkTx, Net);
            if (signArkTx)
            {
                var leafHash = Coin.SpendingScript.LeafHash;
                var sigHash = arkTx.GetSignatureHashTaproot(
                    arkTx.PrecomputeTransactionData([checkpointTxOut]),
                    new TaprootExecutionData(0, leafHash) { SigHash = TaprootSigHash.Default });

                var signature = UserKey.SignBIP340(sigHash.ToBytes(), new byte[32]);

                if (replaceArkTxDestinationAfterSigning)
                {
                    // Same signature, different destination: the payout is rewritten after
                    // the wallet signed the original.
                    var rewritten = arkTx.Clone();
                    rewritten.Outputs[0].ScriptPubKey = NewTaprootScript();
                    arkPsbt = PSBT.FromTransaction(rewritten, Net);
                }

                arkPsbt.Inputs[0].SetTaprootScriptSpendSignature(
                    UserKey.CreateXOnlyPubKey(), leafHash, signature);
            }

            return new PendingArkTransaction(
                arkPsbt.GetGlobalTransaction().GetHash().ToString(),
                arkPsbt.ToBase64(),
                [checkpoint.ToBase64()]);
        }
    }

    private static ArkServerInfo CreateStubServerInfo()
    {
        // Real ArkServerInfo construction in tests requires too many primitives; the
        // RecoveryService only consumes Network, SignerKey and CheckpointTapScript, so the
        // simplest stub is an uninitialized record with those three filled in.
        //
        // Caveat: this bypasses the record constructor, so every other member is left at its
        // default (null for reference types). It holds only while the service reads exactly
        // those three. If ArkServerInfo grows a member with a non-trivial invariant, or the
        // service starts reading a fourth field, build a real instance here instead of
        // widening the reflection below — a NullReferenceException from an uninitialized
        // field is a confusing way to find that out.
        var info = (ArkServerInfo)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(ArkServerInfo));

        var serverKey = ECPrivKey.Create(RandomUtils.GetBytes(32));
        var serverDescriptor = DescriptorFor(serverKey);

        typeof(ArkServerInfo).GetProperty(nameof(ArkServerInfo.Network))!
            .SetValue(info, Net);
        typeof(ArkServerInfo).GetProperty(nameof(ArkServerInfo.SignerKey))!
            .SetValue(info, serverDescriptor);
        typeof(ArkServerInfo).GetProperty(nameof(ArkServerInfo.CheckpointTapScript))!
            .SetValue(info, new UnilateralPathArkTapScript(new Sequence(144),
                new NofNMultisigTapScript([serverDescriptor.ToXOnlyPubKey()])));
        return info;
    }

    /// <summary>
    /// Test double that overrides the proof-creation and checkpoint-signing paths so
    /// tests don't have to stage a fully-functional real signer, and counts how many
    /// checkpoints were signed (a rejected pending tx must sign none).
    /// </summary>
    private sealed class RecordingPendingTxRecoveryService(
        IClientTransport transport, IWalletStorage walletStorage, IWalletProvider walletProvider,
        IVtxoStorage vtxoStorage, ICoinService coinService)
        : PendingArkTransactionRecoveryService(transport, walletStorage, walletProvider, vtxoStorage, coinService)
    {
        public int SignedCheckpoints { get; private set; }

        protected override Task<(string Proof, string Message)> CreateProofAsync(
            ArkCoin anchor, IArkadeWalletSigner signer, Network network,
            CancellationToken cancellationToken)
            => Task.FromResult(("test-proof", "{\"type\":\"get-pending-tx\",\"expire_at\":0}"));

        protected override Task SignCheckpointAsync(ArkCoin coin, PSBT checkpoint,
            IArkadeWalletSigner signer, CancellationToken cancellationToken)
        {
            SignedCheckpoints++;
            return Task.CompletedTask;
        }
    }
}
