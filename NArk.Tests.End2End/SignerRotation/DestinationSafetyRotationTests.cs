using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Wallets;
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
/// End-to-end proof that the SDK auto-disables a wallet sweep <b>destination</b> once a REAL operator
/// signer rotation makes that destination's encoded signer deprecated. Unlike
/// <see cref="ServerKeyRotationSweeperTests"/> (which fakes a deprecated signer without rotating), this
/// uses arkade-regtest's <c>rotate-signer</c> CLI (PR #30) to rotate the live stack mid-test, exercising
/// the production path: <c>ServerInfoChanged</c> → <see cref="ContractReconciliationService"/> flags the
/// stale destination in wallet metadata and raises <see cref="IDestinationSafetyNotifier.DestinationDisabled"/>.
/// <para>Runs in a dedicated CI job (<c>e2e-rotation</c>) on its own stack: a live rotation recreates
/// arkd-wallet and restarts arkd, which would cascade-fail any test sharing the stack. That job boots the
/// stack on the compose <i>default</i> signer (unpinning <c>.env.regtest</c>) so <c>rotate-signer</c> can
/// actually rotate it. Still marked non-parallel as belt-and-suspenders.</para>
/// </summary>
[NonParallelizable]
[Category("RealRotation")]
public class DestinationSafetyRotationTests
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
    public async Task SweepDestination_IsDisabled_WhenItsSignerIsRotatedAway()
    {
        using var host = BuildHost($"DestRotation_{Guid.NewGuid():N}");
        await host.StartAsync();

        var transport = host.Services.GetRequiredService<IClientTransport>();
        var caching = host.Services.GetRequiredService<CachingClientTransport>();
        var walletStorage = host.Services.GetRequiredService<IWalletStorage>();
        var notifier = host.Services.GetRequiredService<IDestinationSafetyNotifier>();

        var info = await transport.GetServerInfoAsync();
        var signerAtCreation = info.SignerKey.ToXOnlyPubKey();

        // ── A wallet whose sweep destination is keyed to the CURRENT operator signer ─────────────
        // An Arkade address encodes (taproot output key, server signer key) as independent fields, so a
        // fresh output key plus the current signer is a valid destination to pin a staleness check to.
        var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();
        var outputKey = ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes());
        var destination = new ArkAddress(outputKey, signerAtCreation).ToString(isMainnet: false);
        var walletInfo = await WalletFactory.CreateWallet(mnemonic, destination, info);
        await walletStorage.UpsertWallet(walletInfo);
        var walletId = walletInfo.Id;

        // Before the rotation the destination's signer is the current one → not stale → not flagged.
        await Task.Delay(TimeSpan.FromSeconds(3)); // let the WalletSaved-triggered reconcile run
        var before = await walletStorage.GetWalletById(walletId);
        Assert.That(before!.Metadata?.ContainsKey(DestinationSafety.PendingConfirmationMetadataKey) ?? false,
            Is.False, "destination must NOT be flagged before the rotation");

        var disabledTcs = new TaskCompletionSource<DestinationDisabledEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        notifier.DestinationDisabled += (_, e) =>
        {
            if (e.WalletId == walletId) disabledTcs.TrySetResult(e);
        };

        // ── Really rotate the operator signer: the just-used signer moves into the deprecated set ──
        await DockerHelper.RotateSigner(cutoff: "+86400");

        // Force the SDK to observe the new signer set — clears the cache and raises ServerInfoChanged,
        // which ContractReconciliationService reacts to by re-checking every wallet's destination.
        caching.InvalidateServerInfoCache();

        // ── The destination's signer is now deprecated → destination disabled + signalled ───────
        await WaitUntilAsync(async () =>
                (await walletStorage.GetWalletById(walletId))!.Metadata?
                    .ContainsKey(DestinationSafety.PendingConfirmationMetadataKey) == true,
            TimeSpan.FromSeconds(60),
            "destination was not flagged pending-confirmation after the signer rotation");

        var raised = await disabledTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var expectedHex = Convert.ToHexString(signerAtCreation.ToBytes()).ToLowerInvariant();
        Assert.Multiple(() =>
        {
            Assert.That(raised.WalletId, Is.EqualTo(walletId));
            Assert.That(raised.DeprecatedServerKey, Is.EqualTo(expectedHex),
                "the disabled destination must be pinned to the signer that was rotated away");
        });

        await host.StopAsync();
    }

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
