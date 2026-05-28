using BTCPayServer.Lightning;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Blockchain;
using NArk.Core.Models.Options;
using NArk.Core.Services;
using NArk.Core.Wallet;
using NArk.Hosting;
using NArk.Safety.AsyncKeyedLock;
using NArk.Core.Transport;
using NArk.Storage.EfCore.Hosting;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz.Models;
using NArk.Swaps.Models;
using NArk.Swaps.Recovery;
using NArk.Swaps.Services;
using NArk.Tests.End2End.Common;
using NArk.Tests.End2End.Core;
using NArk.Tests.End2End.TestPersistance;
using NBitcoin;

namespace NArk.Tests.End2End.Swaps;

/// <summary>
/// Full real-data recovery round-trip on the production wallet stack: fund an HD
/// wallet, create a boltz reverse swap, then re-import the same mnemonic into a
/// fresh (wiped) storage and assert <see cref="IWalletRecoveryService"/> rebuilds
/// contracts, the derivation index, funds (VTXOs) and swap data. Uses arkd + boltz
/// (<see cref="SharedSwapInfrastructure"/>).
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

    private static async Task ArkSend(string toAddress, long amountSats)
    {
        var result = await Cli.Wrap("docker")
            .WithArguments([
                "exec", "ark", "ark", "send", "--to", toAddress, "--amount",
                amountSats.ToString(), "--password", "secret"
            ])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();
        if (!result.IsSuccess)
            throw new InvalidOperationException(
                $"ark send failed (exit={result.ExitCode}): {result.StandardOutput} {result.StandardError}");
    }

    [Test]
    public async Task FullRecovery_RestoresContracts_Index_Funds_AndSwap()
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
            var vtxoStorage = host1.Services.GetRequiredService<IVtxoStorage>();
            var swapStorage = host1.Services.GetRequiredService<ISwapStorage>();
            var swapMgr = host1.Services.GetRequiredService<SwapsManagementService>();

            var walletInfo = await WalletFactory.CreateWallet(mnemonic, null, serverInfo);
            await walletStorage.UpsertWallet(walletInfo);
            walletId = walletInfo.Id;

            // Fund a receive contract → a plain VTXO (the "funds" to recover).
            var fundedTcs = new TaskCompletionSource();
            vtxoStorage.VtxosChanged += (_, _) => fundedTcs.TrySetResult();
            var contract = await contractService.DeriveContract(walletId, NextContractPurpose.Receive);
            await ArkSend(contract.GetArkAddress().ToString(false), 100_000);
            await fundedTcs.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // Reverse swap (receive Ark via Lightning) → a VHTLC contract + swap record.
            var settledTcs = new TaskCompletionSource();
            swapStorage.SwapsChanged += (_, swap) =>
            {
                if (swap.Status == ArkSwapStatus.Settled)
                    settledTcs.TrySetResult();
            };
            var invoice = await FulmineLiquidityHelper.RetryWithSettle(() =>
                swapMgr.InitiateReverseSwap(
                    walletId,
                    new CreateInvoiceParams(LightMoney.Satoshis(50_000), "recovery-test", TimeSpan.FromHours(1)),
                    CancellationToken.None));
            await Cli.Wrap("docker")
                .WithArguments(["exec", "lnd", "lncli", "--network=regtest", "payinvoice", "--force", invoice])
                .ExecuteBufferedAsync();
            await settledTcs.Task.WaitAsync(TimeSpan.FromMinutes(2));

            // Sanity: the first host now holds contracts, an advanced index, and a swap.
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

        // Swap data restored (the reverse swap is now known + audited).
        Assert.That(report.SwapAudit, Is.Not.Empty, "swap data must be restored");

        await host2.StopAsync();
    }
}
