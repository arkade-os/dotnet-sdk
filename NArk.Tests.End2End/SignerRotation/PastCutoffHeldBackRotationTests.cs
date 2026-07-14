using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Wallets;
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
/// End-to-end proof that a wallet coin whose operator signer is rotated to a <b>past cutoff</b> is HELD BACK
/// (regime 2 of the past-cutoff funds lifecycle): once past the cutoff the operator stops co-signing, so the
/// coin is neither collaboratively spendable nor — until the VTXO tree expires — recoverable.
/// <see cref="SpendingService.GetAvailableCoins"/> excludes it (<see cref="ArkCoin.IsDeprecatedSignerPastCutoff"/>)
/// and the 4c guard in <see cref="SimpleIntentScheduler"/> vetoes it from batches. Uses arkade-regtest's
/// <c>rotate-signer --cutoff -60</c> for a REAL rotation to a cutoff already in the past.
/// <para><b>Why not the full recovery (regime 3) here?</b> Driving a coin to recovery in-test needs a short
/// VTXO expiry, and the only short-expiry config (block-based delays, &lt;512) trips an arkd rc.1 nil-pointer
/// panic in <c>Settings.Digest</c> — so regime 3 cannot be exercised in this harness. The recovery path itself
/// is sound: within cutoff the offchain sweep migrates coins (see <see cref="SweepMigrationRotationTests"/>);
/// past cutoff a coin is held back until expiry, then renews via an intent that arkd accepts once the coin is
/// recoverable. This E2E pins the in-protocol regime-2 (held-back) behaviour; the post-expiry regime-3
/// recovery awaits a working short-expiry config.</para>
/// <para>Runs in the <c>e2e-rotation</c> CI job (tagged <c>RealRotation</c>) on the normal time-based stack —
/// held-back needs no short-expiry config. Marked non-parallel as belt-and-suspenders.</para>
/// </summary>
[NonParallelizable]
[Category("RealRotation")]
public class PastCutoffHeldBackRotationTests
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
            })
            .Build();

    [Test]
    public async Task PastCutoffCoin_IsHeldBack_WhenItsSignerIsRotatedPastCutoff()
    {
        using var host = BuildHost($"PastCutoffHeldBack_{Guid.NewGuid():N}");
        await host.StartAsync();

        var transport = host.Services.GetRequiredService<IClientTransport>();
        var caching = host.Services.GetRequiredService<CachingClientTransport>();
        var walletStorage = host.Services.GetRequiredService<IWalletStorage>();
        var contractService = host.Services.GetRequiredService<IContractService>();
        var intentStorage = host.Services.GetRequiredService<IIntentStorage>();
        var spendingService = host.Services.GetRequiredService<ISpendingService>();

        // The signer the funded coin will be locked under. Never hardcoded: a prior rotation (other tests in
        // this job rotate too) may already have moved the "current" signer, so always read it from the stack.
        var info = await transport.GetServerInfoAsync();
        var signerBeforeRotation = info.SignerKey.ToXOnlyPubKey();

        // ── Wallet + self-fund a spendable coin via an arkd note (no Fulmine) ────
        var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();
        var walletInfo = await WalletFactory.CreateWallet(mnemonic, null, info);
        await walletStorage.UpsertWallet(walletInfo);
        var walletId = walletInfo.Id;

        var batchTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        intentStorage.IntentChanged += (_, intent) =>
        {
            if (intent.State == ArkIntentState.BatchSucceeded)
                batchTcs.TrySetResult();
        };
        var note = await DockerHelper.CreateArkNote(1_000_000);
        await contractService.ImportContract(walletId, ArkNoteContract.Parse(note));
        await batchTcs.Task.WaitAsync(TimeSpan.FromSeconds(120));

        // Before the rotation the funded coin is spendable under the current signer.
        await WaitUntilAsync(async () =>
                (await spendingService.GetAvailableCoins(walletId)).Any(c => IsUnder(c, signerBeforeRotation)),
            TimeSpan.FromSeconds(45),
            "funded coin under the current signer never became available");

        // ── Really rotate the operator signer to a cutoff in the PAST ───────────
        // A cutoff of -60 (60s ago) lands the just-used signer in the deprecated set ALREADY past its
        // collaborative-sweep window, so the operator immediately stops co-signing for it.
        await DockerHelper.RotateSigner(cutoff: "-60");
        caching.InvalidateServerInfoCache();

        // ── Regime 2: held back ─────────────────────────────────────────────────
        // The coin's signer is now past-cutoff-deprecated, so GetAvailableCoins must exclude it
        // (ArkCoin.IsDeprecatedSignerPastCutoff). It is NOT yet recoverable (the VTXO tree has not expired),
        // so it is simply held back — neither collaboratively spendable nor migrated.
        await WaitUntilAsync(async () =>
                !(await spendingService.GetAvailableCoins(walletId)).Any(c => IsUnder(c, signerBeforeRotation)),
            TimeSpan.FromSeconds(45),
            "past-cutoff coin was not held back — it should have been excluded from available coins after the rotation");

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
