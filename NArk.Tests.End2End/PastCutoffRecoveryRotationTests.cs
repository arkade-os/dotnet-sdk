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
/// End-to-end proof of the <b>past-cutoff</b> funds lifecycle after a <b>real</b> operator signer
/// rotation — the two regimes <see cref="SweepMigrationRotationTests"/> (regime 1, within-cutoff
/// collaborative sweep) does not cover.
/// <para>
/// Funds a fresh VTXO under the <i>current</i> signer, then rotates that signer into the deprecated set
/// with a cutoff <b>60s in the PAST</b> (<c>rotate-signer --cutoff -60</c>): the operator immediately
/// refuses to co-sign for the old key, so the coin enters the lifecycle's later regimes:
/// </para>
/// <list type="number">
///   <item><b>Regime 2 (held back).</b> Past the cutoff but before the VTXO tree expires, the coin is
///   neither collaboratively spendable nor yet recoverable. <see cref="SpendingService.GetAvailableCoins"/>
///   excludes it (<see cref="ArkCoin.IsDeprecatedSignerPastCutoff"/>) and the 4c guard in
///   <see cref="SimpleIntentScheduler"/> vetoes it from intent selection (it still
///   <see cref="ArkCoin.RequiresForfeit"/> and arkd will not co-sign the forfeit). The coin simply waits.</item>
///   <item><b>Regime 3 (recovered after expiry).</b> Once the VTXO tree expires the coin becomes
///   <see cref="ArkCoin.IsRecoverable"/>; arkd's on-chain sweeper claims the expired output so it is no
///   longer forfeit-bound, the 4c guard stops vetoing it, and the hosted <see cref="IntentGenerationService"/>
///   re-enrolls it via a recovery batch (the session skips the forfeit) that settles a fresh VTXO under the
///   <i>post-rotation</i> current signer.</item>
/// </list>
/// <para>Runs in a dedicated CI job (<c>e2e-rotation-recovery</c>) on its own stack: a live rotation
/// recreates arkd-wallet and restarts arkd, which would cascade-fail any test sharing the stack. That job
/// also shortens <c>ARKD_VTXO_TREE_EXPIRY</c> to 120s so the expiry → recovery transition happens inside the
/// test window (the committed 1h would never expire in time). Tagged <c>RealRotationExpiry</c> — a SEPARATE
/// category from <c>RealRotation</c> — because it needs that short-expiry stack, not the default one. Still
/// marked non-parallel as belt-and-suspenders.</para>
/// </summary>
[NonParallelizable]
[Category("RealRotationExpiry")]
public class PastCutoffRecoveryRotationTests
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
                // VtxosChanged trigger firing for the now-past-cutoff coin.
                s.Configure<SweeperServiceOptions>(o => o.ForceRefreshInterval = TimeSpan.FromSeconds(5));
            })
            .Build();

    [Test]
    public async Task PastCutoffCoin_IsHeldBack_ThenRecovered_AfterExpiry()
    {
        using var host = BuildHost($"PastCutoffRecovery_{Guid.NewGuid():N}");
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
        var signerBeforeRotation = info.SignerKey.ToXOnlyPubKey();

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

        // ── Mint a FRESH VTXO under the CURRENT signer ──────────────────────────
        // The note's batched output already landed under the current signer; spending to a current-signer
        // payment contract for THIS wallet lands a second, freshly-tracked VTXO under the same signer. The
        // contract is persisted (ImportContract accepts it because it is the current signer) so coin gathering
        // can map the VTXO's script back to its server key. After the rotation below this signer becomes the
        // past-cutoff deprecated one, so this is the coin whose held-back → recovered lifecycle we assert.
        var userSigner = await (await walletProvider.GetAddressProviderAsync(walletId))!.GetNextSigningDescriptor();
        var fundedContract = new ArkPaymentContract(info.SignerKey, info.UnilateralExit, userSigner);
        await contractService.ImportContract(walletId, fundedContract);
        var fundedScript = fundedContract.GetArkAddress().ScriptPubKey.ToHex();

        await spendingService.Spend(walletId,
            [new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(500_000), fundedContract.GetArkAddress())]);

        // The current-signer VTXO must materialise (proves the fresh funding worked) and be spendable — at
        // this point nothing is deprecated, so GetAvailableCoins must report a coin under the current signer.
        await WaitUntilAsync(async () =>
                (await vtxoStorage.GetVtxos(scripts: [fundedScript], includeSpent: true)).Count > 0,
            TimeSpan.FromSeconds(45),
            "current-signer VTXO never appeared — could not fund the coin under test");
        await WaitUntilAsync(async () =>
                (await spendingService.GetAvailableCoins(walletId)).Any(c => IsUnder(c, signerBeforeRotation)),
            TimeSpan.FromSeconds(45),
            "funded coin under the current signer never became available");

        // ── Really rotate the operator signer to a cutoff in the PAST ───────────
        // A cutoff of -60 (60s ago) lands the just-used signer in the deprecated set ALREADY past its
        // collaborative-sweep window: the operator immediately stops co-signing for it. That is exactly
        // regime 2/3 territory, not the regime-1 sweep that a future cutoff (+86400) would trigger.
        await DockerHelper.RotateSigner(cutoff: "-60");

        // Force the SDK to observe the new signer set — clears the cache so GetServerInfoAsync now reports
        // the previous signer as a PAST-cutoff deprecated one, which the spendability / 4c-guard veto act on.
        caching.InvalidateServerInfoCache();

        // ── Regime 2: held back ─────────────────────────────────────────────────
        // Promptly (well before the ~120s VTXO-tree expiry) the past-cutoff coin must drop out of the
        // spendable set: GetAvailableCoins excludes every coin under a past-cutoff deprecated signer
        // (ArkCoin.IsDeprecatedSignerPastCutoff). It is NOT yet recoverable, so it is held back, not migrated.
        await WaitUntilAsync(async () =>
                !(await spendingService.GetAvailableCoins(walletId)).Any(c => IsUnder(c, signerBeforeRotation)),
            TimeSpan.FromSeconds(45),
            "past-cutoff coin was not held back — it should have been excluded from available coins after the rotation");

        // ── Regime 3: recovered once it becomes recoverable ─────────────────────
        // Expiry is evaluated against CHAIN time (block timestamps), not wall-clock — and the regtest
        // auto-miner is far too slow to rely on — so we MINE to advance the chain past the VTXO's ExpiresAt
        // (~120s on this short-expiry stack). Mining also confirms arkd's on-chain sweep of the expired
        // output, which clears the forfeit requirement so the 4c guard stops vetoing the coin; the hosted
        // IntentGenerationService then re-enrolls it into a recovery batch that settles a fresh spendable
        // VTXO under the post-rotation current signer. Re-fetch server info each loop so we compare against
        // the signer the recovery batch actually lands under.
        using (var recoveryCts = new CancellationTokenSource(TimeSpan.FromSeconds(240)))
        {
            while (true)
            {
                await DockerHelper.MineBlocks(2);
                var current = (await transport.GetServerInfoAsync()).SignerKey.ToXOnlyPubKey();
                if ((await spendingService.GetAvailableCoins(walletId)).Any(c => IsUnder(c, current)))
                    break;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), recoveryCts.Token);
                }
                catch (OperationCanceledException)
                {
                    Assert.Fail("past-cutoff coin was not recovered after it became recoverable — no spendable "
                        + "coin reappeared under the post-rotation current signer despite mining past the VTXO expiry");
                    break;
                }
            }
        }

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
