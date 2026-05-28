using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
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

namespace NArk.Tests.End2End.Core;

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
                // AddArkSwapServices is required for WalletRecoveryService (it lives
                // in NArk.Swaps.Recovery and pulls SwapsManagementService) — the
                // boltz client gets registered too but is never invoked. The
                // ctor still parses BoltzUrl as a Uri though, so we have to hand
                // it a syntactically valid placeholder — the host wouldn't even
                // start otherwise.
                s.AddArkSwapServices();
                s.Configure<BoltzClientOptions>(o =>
                {
                    o.BoltzUrl = "http://boltz.test:9001";
                    o.WebsocketUrl = "ws://boltz.test:9004";
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
            var batchTcs = new TaskCompletionSource();
            intentStorage.IntentChanged += (_, intent) =>
            {
                if (intent.State == ArkIntentState.BatchSucceeded)
                    batchTcs.TrySetResult();
            };
            var note = await DockerHelper.CreateArkNote(100_000);
            await contractService.ImportContract(walletId, ArkNoteContract.Parse(note));
            await batchTcs.Task.WaitAsync(TimeSpan.FromMinutes(2));

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
        var report = await recovery.RecoverAsync(walletId);

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
