using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Intents;
using NArk.Abstractions.VTXOs;
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
using NArk.Tests.End2End.Common;
using NArk.Tests.End2End.TestPersistance;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Tests.End2End.Core;

/// <summary>
/// End-to-end proof that <see cref="NArk.Core.Sweeper.ServerKeyRotationSweepPolicy"/> migrates an
/// <b>unsettled</b> VTXO off a signer that becomes deprecated by a <b>real</b> operator rotation.
/// <para>
/// Unlike <see cref="ServerKeyRotationSweeperTests"/> (which fakes a deprecated signer without rotating),
/// this funds a fresh unsettled VTXO under the <i>current</i> signer, then uses arkade-regtest's
/// <c>rotate-signer</c> CLI (PR #30) with a within-cutoff future cutoff to rotate that signer into the
/// deprecated set. The just-funded VTXO is now under a within-cutoff deprecated signer, so the hosted
/// <see cref="SweeperService"/> collaboratively sweeps it onto the new current signer (regime 1 of the
/// rotation funds model). The wallet is self-funded via an arkd note (no Fulmine dependency).
/// </para>
/// <para>Runs in a dedicated CI job (<c>e2e-rotation</c>) on its own stack: a live rotation recreates
/// arkd-wallet and restarts arkd, which would cascade-fail any test sharing the stack. That job boots the
/// stack on the compose <i>default</i> signer (unpinning <c>.env.regtest</c>) so <c>rotate-signer</c> can
/// actually rotate it. Still marked non-parallel as belt-and-suspenders.</para>
/// </summary>
[NonParallelizable]
[Category("RealRotation")]
public class SweepMigrationRotationTests
{
    private static IHost BuildHost(string dbName) =>
        Host.CreateDefaultBuilder([])
            .AddArk()
            .OnCustomGrpcArk(SharedArkInfrastructure.ArkdEndpoint.ToString())
            .WithSafetyService<AsyncSafetyService>()
            .WithIntentScheduler<SimpleIntentScheduler>()
            .WithWalletProvider<DefaultWalletProvider>()
            .ConfigureServices((_, s) =>
            {
                s.AddDbContextFactory<TestDbContext>(o => o.UseInMemoryDatabase(dbName));
                s.AddArkEfCoreStorage<TestDbContext>();
                s.AddNBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
                s.Configure<SimpleIntentSchedulerOptions>(o =>
                {
                    o.Threshold = TimeSpan.FromHours(2);
                    o.ThresholdHeight = 2000;
                });
                s.Configure<IntentGenerationServiceOptions>(o => o.PollInterval = TimeSpan.FromSeconds(5));
                // Re-poll the sweeper on a short interval so the test does not depend solely on the
                // VtxosChanged trigger firing for the now-deprecated-signer coin.
                s.Configure<SweeperServiceOptions>(o => o.ForceRefreshInterval = TimeSpan.FromSeconds(5));
            })
            .Build();

    [Test]
    public async Task SweepMigratesVtxo_WhenItsCurrentSignerIsRotatedAway()
    {
        using var host = BuildHost($"SweepRotation_{Guid.NewGuid():N}");
        await host.StartAsync();

        var transport = host.Services.GetRequiredService<IClientTransport>();
        var caching = host.Services.GetRequiredService<CachingClientTransport>();
        var walletStorage = host.Services.GetRequiredService<IWalletStorage>();
        var contractService = host.Services.GetRequiredService<IContractService>();
        var contractStorage = host.Services.GetRequiredService<IContractStorage>();
        var vtxoStorage = host.Services.GetRequiredService<IVtxoStorage>();
        var intentStorage = host.Services.GetRequiredService<IIntentStorage>();
        var walletProvider = host.Services.GetRequiredService<IWalletProvider>();
        var spendingService = host.Services.GetRequiredService<ISpendingService>();

        // The signer the funded VTXO will be locked under. Never hardcoded: a prior rotation may already
        // have moved the "current" signer to a rotated key, so always read it from the live stack.
        var info = await transport.GetServerInfoAsync();
        var currentServerKey = info.SignerKey.ToXOnlyPubKey();

        // ── Wallet ──────────────────────────────────────────────────────────────
        var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();
        var walletInfo = await WalletFactory.CreateWallet(mnemonic, null, info);
        await walletStorage.UpsertWallet(walletInfo);
        var walletId = walletInfo.Id;

        // ── Self-fund a spendable VTXO via an arkd note (no Fulmine) ─────────────
        var batchTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        intentStorage.IntentChanged += (_, intent) =>
        {
            if (intent.State == ArkIntentState.BatchSucceeded)
                batchTcs.TrySetResult();
        };
        var note = await DockerHelper.CreateArkNote(1_000_000);
        await contractService.ImportContract(walletId, ArkNoteContract.Parse(note));
        await batchTcs.Task.WaitAsync(TimeSpan.FromSeconds(120));

        // ── Mint a FRESH UNSETTLED VTXO under the CURRENT signer ────────────────
        // The note's batched output is settled/spent, so the sweeper (includeSpent:false) ignores it.
        // Spending to a current-signer payment contract for THIS wallet lands a genuine unsettled VTXO the
        // sweeper will consider — once the rotation turns its signer deprecated. The contract is persisted
        // (ImportContract accepts it because it is the current signer) so the sweeper can map the VTXO's
        // script back to its server key.
        var userSigner = await (await walletProvider.GetAddressProviderAsync(walletId))!.GetNextSigningDescriptor();
        var currentContract = new ArkPaymentContract(info.SignerKey, info.UnilateralExit, userSigner);
        await contractService.ImportContract(walletId, currentContract);
        var migratingScript = currentContract.GetArkAddress().ScriptPubKey.ToHex();

        await spendingService.Spend(walletId,
            [new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(500_000), currentContract.GetArkAddress())]);

        // The current-signer VTXO must materialise (proves the fresh funding worked). We intentionally do
        // NOT assert it is unspent here: its 1h expiry is inside the scheduler's 2h threshold, so renewal
        // can settle it within seconds — racy. The post-rotation anchor below (funds under the NEW signer)
        // is what proves migration actually happened after the rotation.
        await WaitUntilAsync(async () =>
                (await vtxoStorage.GetVtxos(scripts: [migratingScript], includeSpent: true)).Count > 0,
            TimeSpan.FromSeconds(45),
            "current-signer VTXO never appeared — could not fund the migrating script");

        // ── Really rotate the operator signer: the just-used signer moves into the deprecated set ──
        // A future cutoff (+86400 = one day out) keeps the now-deprecated signer WITHIN its
        // collaborative-sweep window — exactly the regime ServerKeyRotationSweepPolicy migrates.
        await DockerHelper.RotateSigner(cutoff: "+86400");

        // Force the SDK to observe the new signer set — clears the cache so GetServerInfoAsync now reports
        // the previous signer as a within-cutoff deprecated one, which the sweep policy acts on.
        caching.InvalidateServerInfoCache();

        // ── The rotation-handling migrates it off the now-deprecated signer ─────────
        // The unsettled within-cutoff-deprecated VTXO gets spent (migrated off the deprecated script) by the
        // hosted SweeperService — the path for unsettled deprecated coins (settled ones migrate via renewal).
        await WaitUntilAsync(async () =>
                (await vtxoStorage.GetVtxos(scripts: [migratingScript], includeSpent: true)).Any(v => v.IsSpent()),
            TimeSpan.FromSeconds(120),
            "the now-deprecated-signer VTXO was not migrated off its script after the rotation");

        // Migrated funds must now be spendable under the CURRENT (post-rotation) signer. Migration is async —
        // the old VTXO is spent first, then the migrated one settles under the current signer a batch-round
        // later — so WAIT for the end-state rather than assert synchronously the instant the old coin is spent.
        await WaitUntilAsync(async () =>
            {
                var after = await transport.GetServerInfoAsync();
                return (await spendingService.GetAvailableCoins(walletId))
                    .Any(c => IsUnder(c, after.SignerKey.ToXOnlyPubKey()));
            },
            TimeSpan.FromSeconds(120),
            "swept funds never became spendable under the post-rotation current signer");

        await host.StopAsync();
    }

    private static bool IsUnder(ArkCoin coin, ECXOnlyPubKey serverKey) =>
        coin.Contract.Server is { } s && s.ToXOnlyPubKey().ToBytes().SequenceEqual(serverKey.ToBytes());

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string failureMessage)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (true)
        {
            if (await condition())
                return;
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cts.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail(failureMessage);
                return;
            }
        }
    }
}
