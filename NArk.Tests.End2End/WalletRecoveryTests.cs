using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Recovery;
using NArk.Abstractions.Wallets;
using NArk.Blockchain;
using NArk.Core.Contracts;
using NArk.Core.Models.Options;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Core.Wallet;
using NArk.Hosting;
using NArk.Safety.AsyncKeyedLock;
using NArk.Storage.EfCore.Hosting;
using NArk.Swaps.Boltz.Models;
using NArk.Swaps.Recovery;
using NArk.Tests.End2End.Common;
using NArk.Tests.End2End.Core;
using NArk.Tests.End2End.TestPersistance;
using NBitcoin;

namespace NArk.Tests.End2End.Swaps;

/// <summary>
/// Full real-data recovery round-trip on the production wallet stack: fund an HD
/// wallet via an arkd-minted note (redeemed into a spendable VTXO at an
/// ArkPaymentContract script by the IntentGenerationService), then re-import the
/// same mnemonic into a fresh (wiped) storage and assert
/// <see cref="IWalletRecoveryService"/> rebuilds contracts, the derivation index
/// and funds (VTXOs). Uses arkd only (<see cref="SharedArkInfrastructure"/>) —
/// swap-recovery is covered by the BTCPay plugin's end-to-end suite, since the
/// boltz/Fulmine round-trip is currently too flaky to assert here without
/// turning the SDK CI red on infra wobble.
/// </summary>
public class WalletRecoveryTests
{
    private static IHost BuildHost(string dbName) =>
        Host.CreateDefaultBuilder([])
            .AddArk()
            .OnCustomGrpcArk(SharedArkInfrastructure.ArkdEndpoint.ToString())
            .WithSafetyService<AsyncSafetyService>()
            .WithIntentScheduler<SimpleIntentScheduler>()
            // Production DefaultWalletProvider backed by the EFCore IWalletStorage —
            // so recovery sees a real ArkWalletInfo with an HD account descriptor
            // + LastUsedIndex, and so the IContractTransformer set (which depends
            // on IWalletProvider) resolves through DI without explicit registration.
            .WithWalletProvider<DefaultWalletProvider>()
            .ConfigureServices((_, s) =>
            {
                s.AddDbContextFactory<TestDbContext>(o => o.UseInMemoryDatabase(dbName));
                s.AddArkEfCoreStorage<TestDbContext>();
                s.AddNBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
                // AddArkSwapServices is required for WalletRecoveryService (it
                // lives in NArk.Swaps.Recovery and pulls SwapsManagementService).
                // Point the boltz client at the real fixture endpoint so the
                // recovery service's read-only boltz queries (HD scan's boltz
                // discovery provider + ScanRecoverableSwapsAsync) resolve in a
                // bounded time. This test never creates a swap — that path's
                // flake (nginx 504 from boltz under load) is covered by the
                // BTCPay plugin E2E instead.
                s.AddArkSwapServices();
                s.Configure<BoltzClientOptions>(o =>
                {
                    o.BoltzUrl = SharedSwapInfrastructure.BoltzEndpoint.ToString();
                    o.WebsocketUrl = SharedSwapInfrastructure.BoltzWsEndpoint.ToString();
                });
                s.Configure<SimpleIntentSchedulerOptions>(o =>
                {
                    o.Threshold = TimeSpan.FromHours(2);
                    o.ThresholdHeight = 2000;
                });
                s.Configure<IntentGenerationServiceOptions>(o => o.PollInterval = TimeSpan.FromSeconds(5));
            })
            .Build();

    [Test]
    public async Task FullRecovery_RestoresContracts_Index_AndFunds()
    {
        var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();
        string walletId;

        // ── Phase 1: build real state on a first host ──────────────────────────
        using (var host1 = BuildHost($"Recovery1_{Guid.NewGuid():N}"))
        {
            await host1.StartAsync();

            var transport = host1.Services.GetRequiredService<IClientTransport>();
            var serverInfo = await transport.GetServerInfoAsync();
            var walletStorage = host1.Services.GetRequiredService<IWalletStorage>();
            var contractService = host1.Services.GetRequiredService<IContractService>();
            var intentStorage = host1.Services.GetRequiredService<IIntentStorage>();

            var walletInfo = await WalletFactory.CreateWallet(mnemonic, null, serverInfo);
            await walletStorage.UpsertWallet(walletInfo);
            walletId = walletInfo.Id;

            // Fund via an arkd-minted note imported through the SDK: the
            // IntentGenerationService participates in a batch round, the note is
            // consumed and the output lands at one of the wallet's
            // ArkPaymentContract scripts (HD-derived, so LastUsedIndex advances)
            // — exactly the kind of VTXO IndexerVtxoDiscoveryProvider rediscovers
            // on recovery. Same pattern as NoteTests.CanCompleteBatchWithOnlyOneNote.
            // RunContinuationsAsynchronously is REQUIRED: IIntentStorage.IntentChanged is
            // raised synchronously from inside BatchManagementService's gRPC event-stream
            // thread (HandleBatchFinalizedAsync → SaveIntent). A plain TCS runs the awaiting
            // continuation inline on that thread, so the rest of this test — including
            // host1.StopAsync(), which disposes BatchManagementService and awaits the very
            // stream task that's running the continuation — self-deadlocks. Resuming on the
            // thread pool frees the event-stream thread.
            var batchTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            intentStorage.IntentChanged += (_, intent) =>
            {
                if (intent.State == ArkIntentState.BatchSucceeded)
                    batchTcs.TrySetResult();
            };
            var note = await DockerHelper.CreateArkNote(100_000);
            await contractService.ImportContract(walletId, ArkNoteContract.Parse(note));
            await batchTcs.Task.WaitAsync(TimeSpan.FromSeconds(90));

            // Sanity: the first host now holds contracts and an advanced index.
            var contracts1 = await host1.Services.GetRequiredService<IContractStorage>()
                .GetContracts(walletIds: [walletId]);
            Assert.That(contracts1, Is.Not.Empty);
            Assert.That((await walletStorage.GetWalletById(walletId))!.LastUsedIndex, Is.GreaterThan(0));

            await host1.StopAsync();
        }

        // ── Phase 2: recover into a FRESH host (wiped storage, same mnemonic) ──
        using var host2 = BuildHost($"Recovery2_{Guid.NewGuid():N}");
        await host2.StartAsync();

        var transport2 = host2.Services.GetRequiredService<IClientTransport>();
        var serverInfo2 = await transport2.GetServerInfoAsync();
        var walletStorage2 = host2.Services.GetRequiredService<IWalletStorage>();
        var contractStorage2 = host2.Services.GetRequiredService<IContractStorage>();

        // Re-import the same mnemonic → deterministically the same wallet id + account descriptor.
        var walletInfo2 = await WalletFactory.CreateWallet(mnemonic, null, serverInfo2);
        await walletStorage2.UpsertWallet(walletInfo2);
        Assert.That(walletInfo2.Id, Is.EqualTo(walletId), "re-import must yield the same wallet id");
        Assert.That(await contractStorage2.GetContracts(walletIds: [walletId]), Is.Empty,
            "fresh storage starts with no contracts");

        var recovery = host2.Services.GetRequiredService<IWalletRecoveryService>();
        // Bound the recovery: a tight gap-limit cuts the HD scan's per-index
        // round-trips (indexer + boarding + boltz discovery providers run per
        // index sequentially) and the CT gives the test a deterministic upper
        // bound so a degraded shared fixture never makes us eat the workflow
        // 15-min cap.
        using var recoveryCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var report = await recovery.RecoverAsync(
            walletId, new RecoveryOptions(GapLimit: 5), recoveryCts.Token);

        // Contracts + derivation index recovered.
        var recoveredContracts = await contractStorage2.GetContracts(walletIds: [walletId]);
        Assert.That(recoveredContracts, Is.Not.Empty, "contracts must be recovered");
        Assert.That((await walletStorage2.GetWalletById(walletId))!.LastUsedIndex, Is.GreaterThan(0),
            "derivation index must be restored");

        // Funds (VTXOs) re-synced from the indexer for the recovered scripts.
        Assert.That(report.FundsScriptsSynced, Is.GreaterThan(0), "funds (VTXOs) must be re-synced");

        await host2.StopAsync();
    }
}
