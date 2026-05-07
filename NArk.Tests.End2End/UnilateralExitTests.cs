using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NArk.Abstractions.Batches;
using NArk.Abstractions.Batches.ServerEvents;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Exit;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Safety;
using NArk.Abstractions.Scripts;
using NArk.Abstractions.VirtualTxs;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Blockchain.NBXplorer;
using NArk.Core.Contracts;
using NArk.Core.Enums;
using NArk.Core.Events;
using NArk.Core.Fees;
using NArk.Core.Models.Options;
using NArk.Core.Services;
using NArk.Core.Transformers;
using NArk.Safety.AsyncKeyedLock;
using NArk.Storage.EfCore.Hosting;
using NArk.Storage.EfCore.Storage;
using NArk.Tests.End2End.Common;
using NArk.Tests.End2End.TestPersistance;
using NArk.Transport.GrpcClient;
using NBitcoin;
using NBXplorer;

namespace NArk.Tests.End2End.Core;

/// <summary>
/// End-to-end coverage for the unilateral exit pipeline (PR #39).
///
/// Mirrors the equivalent suites in arkade-os/go-sdk
/// (TestUnilateralExit/{leaf vtxo,preconfirmed vtxo}) and arkade-os/ts-sdk
/// (should unroll / should reject complete-unroll before unilateral exit
/// delay matures / should complete unroll after unilateral exit delay).
///
/// Setup (per-test):
///   1. Derive a boarding contract for a fresh wallet
///   2. Faucet the boarding address via bitcoin-cli sendtoaddress
///   3. Mine 6 blocks to confirm the boarding UTXO
///   4. Sync via Esplora, then run intent generation + batch settlement
///      so the wallet ends up holding a *settled* VTXO whose ancestry chain
///      anchors at a real on-chain commitment tx — the only kind of VTXO
///      that can actually be exited unilaterally.
///   5. Start the exit on that VTXO and assert state-machine transitions.
///
/// The full broadcast → confirm → CSV → claim path requires advancing the
/// chain past UnilateralExit blocks. We exercise that explicitly via
/// DockerHelper.MineBlocks and ProgressExitsAsync polling.
/// </summary>
public class UnilateralExitTests
{
    private const int BoardingAmountSats = 100_000;

    /// <summary>
    /// Smoke test: after a successful batch settle, StartExitAsync creates an
    /// ExitSession in Broadcasting state with the virtual tx branch populated.
    /// </summary>
    [Test]
    public async Task CanStartUnilateralExitForSettledVtxo()
    {
        await using var setup = await SettleAVtxoAsync();

        // Pick the unspent post-settle VTXO (boarding UTXO is now spent).
        var vtxos = await setup.VtxoStorage.GetVtxos();
        var vtxo = vtxos.FirstOrDefault(v => !v.IsSpent() && !v.Unrolled);
        Assert.That(vtxo, Is.Not.Null,
            "Expected an unspent settled VTXO after batch round; got: " +
            string.Join(", ", vtxos.Select(v => $"{v.TransactionId[..8]}..:{v.TransactionOutputIndex} spent={v.IsSpent()} unrolled={v.Unrolled}")));

        var claimAddress = await GetFreshOnchainAddress();

        var sessions = await setup.ExitService.StartExitAsync(
            setup.WalletId,
            [vtxo!.OutPoint],
            claimAddress,
            CancellationToken.None);

        Assert.That(sessions, Has.Count.EqualTo(1));
        var session = sessions[0];
        Assert.That(session.State, Is.EqualTo(ExitSessionState.Broadcasting));
        Assert.That(session.NextTxIndex, Is.EqualTo(0));
        Assert.That(session.WalletId, Is.EqualTo(setup.WalletId));
        Assert.That(session.ClaimAddress, Is.EqualTo(claimAddress.ToString()));

        // Branch must be populated as part of StartExit (EnsureHexPopulatedAsync).
        var branch = await setup.VirtualTxStorage.GetBranchAsync(vtxo.OutPoint);
        Assert.That(branch, Has.Count.GreaterThan(0),
            "Virtual tx branch should be fetched during StartExitAsync");
        Assert.That(branch.All(tx => tx.Hex is not null), Is.True,
            "All virtual txs in the branch should have hex populated (Full mode)");
    }

    /// <summary>
    /// Calling StartExitAsync twice for the same VTXO returns the existing
    /// session rather than creating a duplicate.
    /// </summary>
    [Test]
    public async Task StartExit_IsIdempotentForSameVtxo()
    {
        await using var setup = await SettleAVtxoAsync();
        var vtxos = await setup.VtxoStorage.GetVtxos();
        var vtxo = vtxos.First(v => !v.IsSpent() && !v.Unrolled);
        var claimAddress = await GetFreshOnchainAddress();

        var firstCall = await setup.ExitService.StartExitAsync(
            setup.WalletId, [vtxo.OutPoint], claimAddress, CancellationToken.None);
        var secondCall = await setup.ExitService.StartExitAsync(
            setup.WalletId, [vtxo.OutPoint], claimAddress, CancellationToken.None);

        Assert.That(firstCall, Has.Count.EqualTo(1));
        Assert.That(secondCall, Has.Count.EqualTo(1));
        Assert.That(secondCall[0].Id, Is.EqualTo(firstCall[0].Id),
            "StartExit should return the existing session, not create a duplicate");
    }

    /// <summary>
    /// Equivalent of go-sdk TestUnilateralExit/leaf vtxo and ts-sdk
    /// "should unroll": iteratively progress the exit, mining a block per
    /// step, and assert that the session advances Broadcasting →
    /// AwaitingCsvDelay (every virtual tx confirmed) within a reasonable
    /// budget.
    /// </summary>
    [Test]
    [CancelAfter(180_000)]
    public async Task ProgressExits_AdvancesFromBroadcastingToAwaitingCsvDelay(CancellationToken token)
    {
        await using var setup = await SettleAVtxoAsync();
        var vtxos = await setup.VtxoStorage.GetVtxos();
        var vtxo = vtxos.First(v => !v.IsSpent() && !v.Unrolled);
        var claimAddress = await GetFreshOnchainAddress();

        var sessions = await setup.ExitService.StartExitAsync(
            setup.WalletId, [vtxo.OutPoint], claimAddress, token);
        var sessionId = sessions[0].Id;

        // Drive the state machine. Each iteration: progress (broadcasts what
        // it can), mine 1 block to confirm what's in mempool, observe state.
        // Use GetByVtxoAsync (not GetActiveSessionsAsync) so a Failed
        // session is still surfaced — otherwise we'd silently lose it.
        ExitSession? current = null;
        for (var step = 0; step < 30 && !token.IsCancellationRequested; step++)
        {
            await setup.ExitService.ProgressExitsAsync(token);
            await DockerHelper.MineBlocks(1, token);

            current = await setup.ExitSessionStorage.GetByVtxoAsync(vtxo.OutPoint, token);
            if (current is null) continue;

            TestContext.WriteLine(
                $"[Exit] step={step} state={current.State} nextTxIndex={current.NextTxIndex} " +
                $"retry={current.RetryCount} fail={current.FailReason ?? "-"}");

            if (current.State == ExitSessionState.AwaitingCsvDelay
                || current.State == ExitSessionState.Claimable
                || current.State == ExitSessionState.Claiming
                || current.State == ExitSessionState.Completed)
            {
                break;
            }

            if (current.State == ExitSessionState.Failed)
            {
                Assert.Fail($"Exit session unexpectedly failed: {current.FailReason}");
            }
        }

        Assert.That(current, Is.Not.Null);
        Assert.That(current!.State,
            Is.EqualTo(ExitSessionState.AwaitingCsvDelay)
                .Or.EqualTo(ExitSessionState.Claimable)
                .Or.EqualTo(ExitSessionState.Claiming)
                .Or.EqualTo(ExitSessionState.Completed),
            $"Exit should advance past Broadcasting; final state={current.State}, " +
            $"nextTxIndex={current.NextTxIndex}, retries={current.RetryCount}, " +
            $"fail={current.FailReason ?? "-"}");
    }

    /// <summary>
    /// Equivalent of ts-sdk "should reject complete-unroll before unilateral
    /// exit delay matures": once a session reaches AwaitingCsvDelay, calling
    /// ProgressExitsAsync repeatedly without advancing the chain past the
    /// CSV must NOT promote the session to Claimable. Mining the full
    /// CSV-equivalent block range then promotes it.
    /// </summary>
    [Test]
    [CancelAfter(240_000)]
    public async Task AwaitingCsvDelay_DoesNotAdvanceUntilDelayMatures(CancellationToken token)
    {
        await using var setup = await SettleAVtxoAsync();
        var vtxos = await setup.VtxoStorage.GetVtxos();
        var vtxo = vtxos.First(v => !v.IsSpent() && !v.Unrolled);
        var claimAddress = await GetFreshOnchainAddress();

        var sessions = await setup.ExitService.StartExitAsync(
            setup.WalletId, [vtxo.OutPoint], claimAddress, token);
        var sessionId = sessions[0].Id;

        // 1. Drive to AwaitingCsvDelay (broadcast + 1-block confirms).
        ExitSession? current = null;
        for (var step = 0; step < 30 && !token.IsCancellationRequested; step++)
        {
            await setup.ExitService.ProgressExitsAsync(token);
            await DockerHelper.MineBlocks(1, token);
            current = await setup.ExitSessionStorage.GetByVtxoAsync(vtxo.OutPoint, token);
            if (current?.State is ExitSessionState.AwaitingCsvDelay
                or ExitSessionState.Claimable
                or ExitSessionState.Claiming
                or ExitSessionState.Completed) break;
            if (current?.State is ExitSessionState.Failed)
                Assert.Fail($"Exit failed: {current.FailReason}");
        }

        Assert.That(current?.State, Is.EqualTo(ExitSessionState.AwaitingCsvDelay)
            .Or.EqualTo(ExitSessionState.Claimable)
            .Or.EqualTo(ExitSessionState.Claiming)
            .Or.EqualTo(ExitSessionState.Completed));

        // If we're already past AwaitingCsvDelay (very short CSV in this
        // regtest config), nothing to assert about the rejection — skip.
        if (current!.State != ExitSessionState.AwaitingCsvDelay) return;

        // 2. Without mining further, ProgressExitsAsync several times and
        //    assert state stays at AwaitingCsvDelay (the leaf is confirmed
        //    but the CSV countdown hasn't advanced enough).
        for (var probe = 0; probe < 5; probe++)
        {
            await setup.ExitService.ProgressExitsAsync(token);
            current = await setup.ExitSessionStorage.GetByVtxoAsync(vtxo.OutPoint, token);
            Assert.That(current?.State, Is.EqualTo(ExitSessionState.AwaitingCsvDelay),
                $"Session should not advance to Claimable before CSV delay matures " +
                $"(probe {probe}, observed state={current?.State})");
        }

        // 3. Mine enough blocks to satisfy the CSV delay, then progress.
        var serverInfo = await setup.ClientTransport.GetServerInfoAsync(token);
        var csvBlocks = (int)serverInfo.UnilateralExit.Value + 2;
        TestContext.WriteLine($"[Exit] mining {csvBlocks} blocks to mature CSV delay");
        await DockerHelper.MineBlocks(csvBlocks, token);

        for (var step = 0; step < 10 && !token.IsCancellationRequested; step++)
        {
            await setup.ExitService.ProgressExitsAsync(token);
            current = await setup.ExitSessionStorage.GetByVtxoAsync(vtxo.OutPoint, token);
            if (current?.State is not ExitSessionState.AwaitingCsvDelay) break;
            await Task.Delay(500, token);
        }

        Assert.That(current?.State,
            Is.EqualTo(ExitSessionState.Claimable)
                .Or.EqualTo(ExitSessionState.Claiming)
                .Or.EqualTo(ExitSessionState.Completed),
            $"After mining past CSV delay, session should advance from AwaitingCsvDelay; " +
            $"observed state={current?.State}, fail={current?.FailReason ?? "-"}");
    }

    // ----- helpers --------------------------------------------------------

    /// <summary>
    /// Boards onchain funds into the wallet, runs intent generation + batch
    /// settlement, and wires up the unilateral-exit dependency graph against
    /// the same TestStorage so all subsequent service calls share state.
    /// Returns a disposable holding everything tests need to drive the exit.
    /// </summary>
    private static async Task<ExitTestSetup> SettleAVtxoAsync()
    {
        // ---- wallet + storage + transport ----
        // Build a ServiceCollection so the standard DI graph + the unilateral
        // exit storages share a single InMemory DbContextFactory.
        var safetyService = new AsyncSafetyService();
        var dbName = $"ExitTest_{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContextFactory<TestDbContext>(options => options.UseInMemoryDatabase(dbName));
        services.AddSingleton<ISafetyService>(safetyService);
        services.AddArkEfCoreStorage<TestDbContext>();
        services.AddSingleton<EfCoreVirtualTxStorage>();
        services.AddSingleton<IVirtualTxStorage>(sp => sp.GetRequiredService<EfCoreVirtualTxStorage>());
        services.AddSingleton<EfCoreExitSessionStorage>();
        services.AddSingleton<IExitSessionStorage>(sp => sp.GetRequiredService<EfCoreExitSessionStorage>());
        var sp = services.BuildServiceProvider();

        var vtxoStorage = sp.GetRequiredService<IVtxoStorage>();
        var contractStorage = sp.GetRequiredService<IContractStorage>();
        var intentStorage = sp.GetRequiredService<IIntentStorage>();
        var virtualTxStorage = sp.GetRequiredService<IVirtualTxStorage>();
        var exitSessionStorage = sp.GetRequiredService<IExitSessionStorage>();

        // Wait for the post-batch offchain VTXO to land in storage. The
        // boarding UTXO is Unrolled=true and gets consumed by the batch;
        // the new offchain output is Unrolled=false and must come through
        // VtxoSynchronizationService's subscription stream below.
        var settledVtxoTcs = new TaskCompletionSource();
        vtxoStorage.VtxosChanged += (_, vtxo) =>
        {
            if (!vtxo.IsSpent() && !vtxo.Unrolled) settledVtxoTcs.TrySetResult();
        };

        var clientTransport = new GrpcClientTransport(SharedArkInfrastructure.ArkdEndpoint.ToString());
        var info = await clientTransport.GetServerInfoAsync();

        var walletProvider = new InMemoryWalletProvider(clientTransport);
        var walletId = await walletProvider.CreateTestWallet();
        var contractService = new ContractService(walletProvider, contractStorage, clientTransport);

        // Stream VTXO updates from arkd into vtxoStorage so the post-batch
        // offchain VTXO becomes visible without manual indexer polling.
        var vtxoSync = new VtxoSynchronizationService(
            vtxoStorage, clientTransport,
            [(IActiveScriptsProvider)vtxoStorage, (IActiveScriptsProvider)contractStorage]);
        await vtxoSync.StartAsync(CancellationToken.None);

        // ---- board: derive boarding contract, faucet, confirm, sync ----
        var boardingContract = (ArkBoardingContract)await contractService.DeriveContract(
            walletId, NextContractPurpose.Boarding, ContractActivityState.Active);
        var onchainAddress = boardingContract.GetOnchainAddress(info.Network).ToString();
        var btcAmount = (BoardingAmountSats / 100_000_000m)
            .ToString("0.########", System.Globalization.CultureInfo.InvariantCulture);
        var fundingTxid = (await DockerHelper.Exec(
            "bitcoin", ["bitcoin-cli", "-rpcwallet=", "sendtoaddress", onchainAddress, btcAmount])).Trim();
        Assert.That(fundingTxid, Is.Not.Empty, "sendtoaddress should return a txid");

        await DockerHelper.MineBlocks(6);

        var utxoProvider = new EsploraBoardingUtxoProvider(SharedArkInfrastructure.ChopsticksEndpoint);
        var boardingSync = new BoardingUtxoSyncService(
            contractStorage, vtxoStorage, clientTransport, utxoProvider);

        ArkVtxo? syncedBoarding = null;
        for (var i = 0; i < 10 && syncedBoarding is null; i++)
        {
            await boardingSync.SyncAsync();
            syncedBoarding = (await vtxoStorage.GetVtxos())
                .FirstOrDefault(v => v.TransactionId == fundingTxid);
            if (syncedBoarding is null) await Task.Delay(TimeSpan.FromSeconds(2));
        }
        Assert.That(syncedBoarding, Is.Not.Null, "Boarding UTXO should sync via Esplora");

        // ---- settle: intent gen + submit + batch session ----
        var chainTimeProvider = new ChainTimeProvider(info.Network, SharedArkInfrastructure.NbxplorerEndpoint);
        var coinService = new CoinService(clientTransport, contractStorage,
        [
            new PaymentContractTransformer(walletProvider),
            new BoardingContractTransformer(walletProvider),
        ]);

        var newSuccessBatch = new TaskCompletionSource();
        intentStorage.IntentChanged += (_, intent) =>
        {
            if (intent.State == ArkIntentState.BatchSucceeded)
                newSuccessBatch.TrySetResult();
        };

        var scheduler = new SimpleIntentScheduler(
            new DefaultFeeEstimator(clientTransport, chainTimeProvider),
            clientTransport,
            contractService,
            chainTimeProvider,
            new OptionsWrapper<SimpleIntentSchedulerOptions>(new SimpleIntentSchedulerOptions
            {
                Threshold = TimeSpan.FromHours(25),
                ThresholdHeight = 200,
            }));

        var intentGeneration = new IntentGenerationService(
            clientTransport,
            new DefaultFeeEstimator(clientTransport, chainTimeProvider),
            coinService,
            walletProvider,
            intentStorage,
            safetyService,
            contractStorage,
            vtxoStorage,
            scheduler,
            new OptionsWrapper<IntentGenerationServiceOptions>(
                new IntentGenerationServiceOptions { PollInterval = TimeSpan.FromHours(5) }));
        await intentGeneration.StartAsync(CancellationToken.None);

        var intentSync = new IntentSynchronizationService(intentStorage, clientTransport, safetyService);
        await intentSync.StartAsync(CancellationToken.None);

        // SimpleIntentScheduler derives the SendToSelf output contract as
        // Inactive — PostBatchVtxoPollingHandler's isActive=true filter
        // would skip it, so wire a custom handler that polls *all* wallet
        // contracts (active or not) right after BatchFinalized. This is
        // exactly the gap the test is trying to verify isn't reached.
        var batchPolledTcs = new TaskCompletionSource();
        var postBatchHandler = new InlineEventHandler<PostBatchSessionEvent>(async (evt, ct) =>
        {
            if (evt.State != ActionState.Successful) return;
            var allContracts = await contractStorage.GetContracts(
                walletIds: [evt.Intent.WalletId], cancellationToken: ct);
            var allScripts = allContracts.Select(c => c.Script).ToHashSet();
            if (allScripts.Count == 0)
            {
                batchPolledTcs.TrySetResult();
                return;
            }
            // arkd commits the new VTXO to its indexer somewhere between
            // 0–10 seconds after BatchFinalized. Probe at a schedule that
            // covers that window without spinning hot. Bail early once the
            // !Unrolled VTXO has been observed (settledVtxoTcs) so we
            // don't keep polling after the test has moved on.
            foreach (var delay in new[] { 500, 1500, 3000, 5000, 8000 })
            {
                await Task.Delay(TimeSpan.FromMilliseconds(delay), ct);
                await vtxoSync.PollScriptsForVtxos(allScripts, after: null, ct);
                if (settledVtxoTcs.Task.IsCompleted) break;
            }
            batchPolledTcs.TrySetResult();
        });

        var batchManager = new BatchManagementService(
            intentStorage, clientTransport, vtxoStorage, contractStorage,
            walletProvider, coinService, safetyService,
            new IEventHandler<PostBatchSessionEvent>[] { postBatchHandler });
        await batchManager.StartAsync(CancellationToken.None);

        await newSuccessBatch.Task.WaitAsync(TimeSpan.FromMinutes(2));
        // Wait until the post-batch poll completes AND the new !Unrolled
        // VTXO has been observed via VtxosChanged. The poll itself can
        // sometimes return empty if arkd's indexer hasn't committed yet —
        // give it a few extra seconds via VtxoSync's RoutinePoll.
        await batchPolledTcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await settledVtxoTcs.Task.WaitAsync(TimeSpan.FromSeconds(60));

        // ---- exit-side dependencies ----
        var explorerClient = new ExplorerClient(
            new NBXplorerNetworkProvider(info.Network.ChainName).GetBTC(),
            SharedArkInfrastructure.NbxplorerEndpoint);
        var broadcaster = new NBXplorerOnchainBroadcaster(explorerClient);
        var virtualTxService = new VirtualTxService(clientTransport, virtualTxStorage);
        var exitService = new UnilateralExitService(
            clientTransport,
            virtualTxStorage,
            exitSessionStorage,
            vtxoStorage,
            contractStorage,
            broadcaster,
            walletProvider,
            chainTimeProvider,
            virtualTxService,
            feeWallet: null);

        return new ExitTestSetup(
            walletId,
            vtxoStorage,
            virtualTxStorage,
            exitSessionStorage,
            clientTransport,
            exitService,
            new IAsyncDisposable[]
            {
                intentGeneration, intentSync, batchManager, vtxoSync,
            });
    }

    private static async Task<BitcoinAddress> GetFreshOnchainAddress()
    {
        var addr = (await DockerHelper.Exec(
            "bitcoin", ["bitcoin-cli", "-rpcwallet=", "getnewaddress"])).Trim();
        Assert.That(addr, Is.Not.Empty, "bitcoin-cli getnewaddress returned empty");
        return BitcoinAddress.Create(addr, Network.RegTest);
    }

    /// <summary>
    /// Minimal IEventHandler shim that delegates to a lambda. Lets a test
    /// inject post-batch behaviour without authoring a full handler class.
    /// </summary>
    private sealed class InlineEventHandler<T>(Func<T, CancellationToken, Task> handle) : IEventHandler<T> where T : class
    {
        public Task HandleAsync(T @event, CancellationToken cancellationToken = default)
            => handle(@event, cancellationToken);
    }

    private sealed class ExitTestSetup(
        string walletId,
        IVtxoStorage vtxoStorage,
        IVirtualTxStorage virtualTxStorage,
        IExitSessionStorage exitSessionStorage,
        NArk.Core.Transport.IClientTransport clientTransport,
        UnilateralExitService exitService,
        IReadOnlyCollection<IAsyncDisposable> disposables) : IAsyncDisposable
    {
        public string WalletId { get; } = walletId;
        public IVtxoStorage VtxoStorage { get; } = vtxoStorage;
        public IVirtualTxStorage VirtualTxStorage { get; } = virtualTxStorage;
        public IExitSessionStorage ExitSessionStorage { get; } = exitSessionStorage;
        public NArk.Core.Transport.IClientTransport ClientTransport { get; } = clientTransport;
        public UnilateralExitService ExitService { get; } = exitService;

        public async ValueTask DisposeAsync()
        {
            foreach (var d in disposables)
            {
                try { await d.DisposeAsync(); } catch { /* best effort */ }
            }
        }
    }

}
