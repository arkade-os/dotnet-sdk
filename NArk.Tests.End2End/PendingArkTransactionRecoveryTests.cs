using CliWrap;
using NArk.Abstractions;
using NArk.Abstractions.Batches;
using NArk.Abstractions.Batches.ServerEvents;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core;
using NArk.Core.Contracts;
using NArk.Core.Services;
using NArk.Core.Transformers;
using NArk.Core.Transport;
using NArk.Core.Transport.Models;
using NArk.Tests.End2End.Common;
using NArk.Tests.End2End.TestPersistance;
using NBitcoin;

namespace NArk.Tests.End2End.Core;

public class PendingArkTransactionRecoveryTests
{
    /// <summary>
    /// Simulates a process crash between the SDK's Submit and Finalize phases,
    /// then runs <see cref="PendingArkTransactionRecoveryService"/> against the
    /// real arkd and asserts that the stranded pending transaction is recovered
    /// and finalized.
    /// </summary>
    [Test]
    public async Task RecoversPendingTx_WhenFinalizeNeverFollowedSubmit()
    {
        var walletDetails = await FundedWalletHelper.GetFundedWallet();
        await using var vtxoSync = walletDetails.vtxoSync;

        var realTransport = walletDetails.clientTransport;
        var crashTransport = new CrashAfterSubmitTransport(realTransport);

        var coinService = new CoinService(realTransport, walletDetails.contracts,
            [new PaymentContractTransformer(walletDetails.walletProvider),
             new HashLockedContractTransformer(walletDetails.walletProvider)]);

        // Self-spend half the balance with the crashing transport so Submit reaches
        // arkd (locks the input as in-flight) but Finalize never runs.
        var receiveContract = await walletDetails.contractService.DeriveContract(
            walletDetails.walletIdentifier, NextContractPurpose.Receive);

        var crashSpending = new SpendingService(walletDetails.vtxoStorage, walletDetails.contracts,
            walletDetails.walletProvider, coinService, walletDetails.contractService, crashTransport,
            new NArk.Core.CoinSelector.DefaultCoinSelector(), walletDetails.safetyService, TestStorage.CreateIntentStorage());

        Exception? caught = null;
        try
        {
            await crashSpending.Spend(walletDetails.walletIdentifier,
            [
                new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(100_000), receiveContract.GetArkAddress())
            ]);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        Assert.That(caught, Is.Not.Null,
            "Spend with FinalizeTx-crashing transport should bubble the simulated crash");
        Assert.That(crashTransport.SubmitCallCount, Is.GreaterThanOrEqualTo(1),
            "SubmitTx must have reached arkd before the simulated Finalize crash");
        Assert.That(crashTransport.FinalizeAttempts, Is.GreaterThanOrEqualTo(1),
            "FinalizeTx must have been attempted (so the inputs are now server-pending)");

        // Diagnostic: dump the wallet's VTXO outpoints and what arkd reports for them.
        // If the server isn't holding our pending tx (projection lag, signature mismatch,
        // wrong outpoint format), this surfaces the gap directly.
        var allVtxos = (await walletDetails.vtxoStorage.GetVtxos(
            walletIds: [walletDetails.walletIdentifier],
            includeSpent: true)).ToList();
        TestContext.Out.WriteLine($"[recovery diag] wallet has {allVtxos.Count} local VTXO(s):");
        foreach (var v in allVtxos)
            TestContext.Out.WriteLine($"  - {v.OutPoint} swept={v.Swept} spent_by={v.SpentByTransactionId ?? "(null)"} settled_by={v.SettledByTransactionId ?? "(null)"} ark_tx={v.ArkTxid ?? "(null)"}");
        TestContext.Out.WriteLine($"[recovery diag] crash transport: Submit={crashTransport.SubmitCallCount}, Finalize attempts={crashTransport.FinalizeAttempts}");
        TestContext.Out.WriteLine($"[recovery diag] crash exception: {caught!.GetType().Name}: {caught.Message}");

        // Ask arkd directly what it thinks of these outpoints. If arkd sees them as spent
        // (with ark_txid set), the SDK proof should match a pending tx; if arkd sees
        // them as unspent, the projection hasn't fired and recovery has nothing to do.
        var arkdView = new List<ArkVtxo>();
        await foreach (var vtxo in realTransport.GetVtxosByOutpoints(
            allVtxos.Select(v => v.OutPoint).ToArray()))
        {
            arkdView.Add(vtxo);
        }
        TestContext.Out.WriteLine($"[recovery diag] arkd reports {arkdView.Count} VTXO(s) for those outpoints:");
        foreach (var v in arkdView)
            TestContext.Out.WriteLine(
                $"  - {v.OutPoint} spent_by={v.SpentByTransactionId ?? "(null)"} settled_by={v.SettledByTransactionId ?? "(null)"} " +
                $"ark_tx={v.ArkTxid ?? "(null)"} swept={v.Swept} unrolled={v.Unrolled} preconfirmed={v.Preconfirmed}");

        // Look for any VTXO at the pending arkTxId — if arkd has already created
        // output VTXOs at that txid, the NOT EXISTS clause in GetPendingSpentVtxos
        // is broken and recovery will silently see an empty list.
        var pendingArkTxId = arkdView.FirstOrDefault()?.ArkTxid;
        if (!string.IsNullOrEmpty(pendingArkTxId))
        {
            var outputProbes = new[]
            {
                new OutPoint(uint256.Parse(pendingArkTxId), 0),
                new OutPoint(uint256.Parse(pendingArkTxId), 1),
                new OutPoint(uint256.Parse(pendingArkTxId), 2),
            };
            var hits = new List<ArkVtxo>();
            await foreach (var v in realTransport.GetVtxosByOutpoints(outputProbes))
                hits.Add(v);
            TestContext.Out.WriteLine($"[recovery diag] vtxos at pending tx {pendingArkTxId}: {hits.Count}");
            foreach (var h in hits)
                TestContext.Out.WriteLine($"  - {h.OutPoint} spent_by={h.SpentByTransactionId ?? "(null)"} settled_by={h.SettledByTransactionId ?? "(null)"}");
        }

        // Now run recovery against the real arkd. Recovery must:
        //   1. authenticate with a BIP-322 proof anchored on a wallet VTXO,
        //   2. retrieve the pending tx the server is holding,
        //   3. sign the checkpoints with the wallet signer,
        //   4. call FinalizeTx and report the arkTxId in its result.
        var recovery = new PendingArkTransactionRecoveryService(
            realTransport,
            walletStorage: NoOpWalletStorage.Instance,
            walletDetails.walletProvider,
            walletDetails.vtxoStorage,
            coinService);

        var failures = new List<PendingTxRecoveryFailureEventArgs>();
        recovery.RecoveryFailed += (_, e) => failures.Add(e);

        // arkd's "spent_by_pending" projection that exposes the VTXO via GetPendingTx
        // runs async (watermill event bus) — give it up to 10s to catch up before
        // declaring the recovery a failure. Production never races this projection
        // because recovery runs at host startup, well after the crash.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        IReadOnlyList<string> finalized = [];
        var attempts = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            attempts++;
            finalized = await recovery.FinalizePendingArkTransactionsAsync(
                walletDetails.walletIdentifier, CancellationToken.None);
            if (finalized.Count > 0) break;
            await Task.Delay(250);
        }
        TestContext.Out.WriteLine(
            $"[recovery diag] {attempts} recovery attempt(s); finalized={finalized.Count}; failures={failures.Count}");
        if (failures.Count > 0)
        {
            foreach (var f in failures)
                TestContext.Out.WriteLine($"  - failure {f.ArkTxId}: {f.Exception.GetType().Name}: {f.Exception.Message}");
        }

        Assert.That(failures, Is.Empty,
            $"No per-tx failures expected, got: {string.Join(", ", failures.Select(f => f.Exception.Message))}");
        Assert.That(finalized, Is.Not.Empty,
            "Recovery must finalize at least one pending arkTxId once arkd's projection catches up");

        // Idempotency: a second recovery call observes nothing left to finalize —
        // the server only returns pending txs that are still in flight.
        var second = await recovery.FinalizePendingArkTransactionsAsync(
            walletDetails.walletIdentifier, CancellationToken.None);
        Assert.That(second, Is.Empty,
            "Second recovery sweep should be a no-op once the pending tx has been finalized");
    }

    /// <summary>
    /// Decorator that lets every transport call through except <see cref="FinalizeTx"/>,
    /// which throws on the FIRST invocation to simulate a process crash between Submit
    /// and Finalize. Tracks call counts so the test can assert that Submit really
    /// reached the server (the precondition for a stranded pending tx).
    /// </summary>
    private sealed class CrashAfterSubmitTransport : IClientTransport
    {
        private readonly IClientTransport _inner;
        private int _finalizeAttempts;

        public CrashAfterSubmitTransport(IClientTransport inner) => _inner = inner;

        public int SubmitCallCount { get; private set; }
        public int FinalizeAttempts => _finalizeAttempts;

        public Task<SubmitTxResponse> SubmitTx(string signedArkTx, string[] checkpointTxs,
            CancellationToken cancellationToken = default)
        {
            SubmitCallCount++;
            return _inner.SubmitTx(signedArkTx, checkpointTxs, cancellationToken);
        }

        public Task FinalizeTx(string arkTxId, string[] finalCheckpointTxs, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _finalizeAttempts);
            throw new InvalidOperationException("simulated crash between Submit and Finalize");
        }

        // Pure pass-through for everything else.
        public Task<ArkServerInfo> GetServerInfoAsync(CancellationToken cancellationToken = default)
            => _inner.GetServerInfoAsync(cancellationToken);

        public IAsyncEnumerable<HashSet<string>> GetVtxoToPollAsStream(IReadOnlySet<string> scripts, CancellationToken token = default)
            => _inner.GetVtxoToPollAsStream(scripts, token);

        public IAsyncEnumerable<ArkVtxo> GetVtxoByScriptsAsSnapshot(IReadOnlySet<string> scripts, CancellationToken cancellationToken = default)
            => _inner.GetVtxoByScriptsAsSnapshot(scripts, cancellationToken);

        public IAsyncEnumerable<ArkVtxo> GetVtxoByScriptsAsSnapshot(IReadOnlySet<string> scripts,
            DateTimeOffset? after, DateTimeOffset? before, CancellationToken cancellationToken = default)
            => _inner.GetVtxoByScriptsAsSnapshot(scripts, after, before, cancellationToken);

        public IAsyncEnumerable<ArkVtxo> GetVtxosByOutpoints(IReadOnlyCollection<OutPoint> outpoints,
            bool spentOnly = false, CancellationToken cancellationToken = default)
            => _inner.GetVtxosByOutpoints(outpoints, spentOnly, cancellationToken);

        public Task<string> RegisterIntent(ArkIntent intent, CancellationToken cancellationToken = default)
            => _inner.RegisterIntent(intent, cancellationToken);

        public Task DeleteIntent(ArkIntent intent, CancellationToken cancellationToken = default)
            => _inner.DeleteIntent(intent, cancellationToken);

        public Task SubmitTreeNoncesAsync(SubmitTreeNoncesRequest treeNonces, CancellationToken cancellationToken)
            => _inner.SubmitTreeNoncesAsync(treeNonces, cancellationToken);

        public Task SubmitTreeSignaturesRequest(SubmitTreeSignaturesRequest treeSigs, CancellationToken cancellationToken)
            => _inner.SubmitTreeSignaturesRequest(treeSigs, cancellationToken);

        public Task SubmitSignedForfeitTxsAsync(SubmitSignedForfeitTxsRequest req, CancellationToken cancellationToken)
            => _inner.SubmitSignedForfeitTxsAsync(req, cancellationToken);

        public Task ConfirmRegistrationAsync(string intentId, CancellationToken cancellationToken)
            => _inner.ConfirmRegistrationAsync(intentId, cancellationToken);

        public IAsyncEnumerable<BatchEvent> GetEventStreamAsync(GetEventStreamRequest req, CancellationToken cancellationToken)
            => _inner.GetEventStreamAsync(req, cancellationToken);

        public Task<ArkAssetDetails> GetAssetDetailsAsync(string assetId, CancellationToken cancellationToken = default)
            => _inner.GetAssetDetailsAsync(assetId, cancellationToken);

        public Task UpdateStreamTopicsAsync(string streamId, string[]? addTopics, string[]? removeTopics, CancellationToken cancellationToken = default)
            => _inner.UpdateStreamTopicsAsync(streamId, addTopics, removeTopics, cancellationToken);

        public Task<ArkIntent[]> GetIntentsByProofAsync(string proof, string message, CancellationToken cancellationToken = default)
            => _inner.GetIntentsByProofAsync(proof, message, cancellationToken);

        public Task<PendingArkTransaction[]> GetPendingTxAsync(string proof, string message,
            CancellationToken cancellationToken = default)
            => _inner.GetPendingTxAsync(proof, message, cancellationToken);
    }

    /// <summary>
    /// Stub for the recovery service constructor: only <see cref="RecoverAllWalletsAsync"/>
    /// touches <see cref="IWalletStorage"/>, and this test calls the per-wallet entry
    /// point directly. Any storage method invocation indicates the SUT regressed into
    /// a code path that shouldn't run from the per-wallet API — fail loudly.
    /// </summary>
    private sealed class NoOpWalletStorage : IWalletStorage
    {
        public static readonly NoOpWalletStorage Instance = new();
#pragma warning disable CS0067
        public event EventHandler<ArkWalletInfo>? WalletSaved;
        public event EventHandler<string>? WalletDeleted;
#pragma warning restore CS0067

        private static InvalidOperationException Unexpected([System.Runtime.CompilerServices.CallerMemberName] string? caller = null)
            => new($"Per-wallet recovery test must not invoke IWalletStorage.{caller}");

        public Task<ArkWalletInfo> LoadWallet(string walletIdentifierOrFingerprint, CancellationToken ct = default) => throw Unexpected();
        public Task<IReadOnlySet<ArkWalletInfo>> LoadAllWallets(CancellationToken ct = default) => throw Unexpected();
        public Task SaveWallet(ArkWalletInfo wallet, CancellationToken ct = default) => throw Unexpected();
        public Task UpdateLastUsedIndex(string walletId, int lastUsedIndex, CancellationToken ct = default) => throw Unexpected();
        public Task<ArkWalletInfo?> GetWalletById(string walletId, CancellationToken ct = default) => throw Unexpected();
        public Task<IReadOnlyList<ArkWalletInfo>> GetWalletsByIds(IEnumerable<string> walletIds, CancellationToken ct = default) => throw Unexpected();
        public Task<bool> UpsertWallet(ArkWalletInfo wallet, bool updateIfExists = true, CancellationToken ct = default) => throw Unexpected();
        public Task<bool> DeleteWallet(string walletId, CancellationToken ct = default) => throw Unexpected();
        public Task UpdateDestination(string walletId, string? destination, CancellationToken ct = default) => throw Unexpected();
        public Task SetMetadataValue(string walletId, string key, string? value, CancellationToken ct = default) => throw Unexpected();
    }
}
