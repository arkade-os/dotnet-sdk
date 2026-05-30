using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NArk.Abstractions.Intents;
using NArk.Core.Contracts;
using NArk.Core.Models.Options;
using NArk.Core.Services;
using NArk.Hosting;
using NArk.Safety.AsyncKeyedLock;
using NArk.Storage.EfCore.Hosting;
using NArk.Tests.End2End.Common;
using NArk.Tests.End2End.TestPersistance;
using NBitcoin;

namespace NArk.Tests.End2End.Core;

public class NoteTests
{
    [Test]
    public async Task CanCompleteBatchWithOnlyOneNote()
    {
        var arkHost =
            Host.CreateDefaultBuilder([])
            .AddArk()
            .OnCustomGrpcArk(SharedArkInfrastructure.ArkdEndpoint.ToString())
            .WithSafetyService<AsyncSafetyService>()
            .WithIntentScheduler<SimpleIntentScheduler>()
            .WithWalletProvider<InMemoryWalletProvider>()
            .ConfigureServices((_, s) =>
            {
                s.AddDbContextFactory<TestDbContext>(options =>
                    options.UseInMemoryDatabase($"Test_{Guid.NewGuid():N}"));
                s.AddArkEfCoreStorage<TestDbContext>();
                s.AddNBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
            })
            .ConfigureServices(s => s.Configure<SimpleIntentSchedulerOptions>(o =>
            {
                o.Threshold = TimeSpan.FromHours(2);
                o.ThresholdHeight = 2000;
            }))
            .ConfigureServices(s => s.Configure<IntentGenerationServiceOptions>(o => o.PollInterval = TimeSpan.FromSeconds(5)))
            .Build();

        await arkHost.StartAsync();

        var contractService = arkHost.Services.GetRequiredService<IContractService>();
        var wallet = arkHost.Services.GetRequiredService<InMemoryWalletProvider>();
        var intentStorage = arkHost.Services.GetRequiredService<IIntentStorage>();

        var note = await DockerHelper.CreateArkNote();

        if (string.IsNullOrEmpty(note))
            throw new Exception("Note creation failed!");

        var fp = await wallet.CreateTestWallet();

        await contractService.ImportContract(fp, ArkNoteContract.Parse(note));

        var gotBatchTcs = new TaskCompletionSource();

        intentStorage.IntentChanged += (_, intent) =>
        {
            if (intent.State == ArkIntentState.BatchSucceeded)
                gotBatchTcs.TrySetResult();
        };

        await gotBatchTcs.Task.WaitAsync(TimeSpan.FromMinutes(1));

        await arkHost.StopAsync();
    }

    /// <summary>
    /// Redeeming an Arkade note a second time must be rejected. After the first redemption
    /// settles in a batch, the note's VTXO is spent on-chain. Attempting to register a new
    /// intent for the same note causes arkd to respond with "VTXO already spent", which the
    /// intent synchronization service converts into a Cancelled intent.
    /// </summary>
    [Test]
    public async Task NoteRedemption_IsRejected_WhenNoteAlreadyRedeemed()
    {
        var arkHost =
            Host.CreateDefaultBuilder([])
            .AddArk()
            .OnCustomGrpcArk(SharedArkInfrastructure.ArkdEndpoint.ToString())
            .WithSafetyService<AsyncSafetyService>()
            .WithIntentScheduler<SimpleIntentScheduler>()
            .WithWalletProvider<InMemoryWalletProvider>()
            .ConfigureServices((_, s) =>
            {
                s.AddDbContextFactory<TestDbContext>(options =>
                    options.UseInMemoryDatabase($"Test_{Guid.NewGuid():N}"));
                s.AddArkEfCoreStorage<TestDbContext>();
                s.AddNBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
            })
            .ConfigureServices(s => s.Configure<SimpleIntentSchedulerOptions>(o =>
            {
                o.Threshold = TimeSpan.FromHours(2);
                o.ThresholdHeight = 2000;
            }))
            .ConfigureServices(s => s.Configure<IntentGenerationServiceOptions>(o => o.PollInterval = TimeSpan.FromSeconds(5)))
            .Build();

        await arkHost.StartAsync();

        var contractService = arkHost.Services.GetRequiredService<IContractService>();
        var wallet = arkHost.Services.GetRequiredService<InMemoryWalletProvider>();
        var intentStorage = arkHost.Services.GetRequiredService<IIntentStorage>();

        var note = await DockerHelper.CreateArkNote();
        if (string.IsNullOrEmpty(note))
            throw new Exception("Note creation failed!");

        var fp = await wallet.CreateTestWallet();
        await contractService.ImportContract(fp, ArkNoteContract.Parse(note));

        // ── First redemption: wait for BatchSucceeded ──────────────────────────────
        var firstBatchTcs = new TaskCompletionSource<ArkIntent>();
        intentStorage.IntentChanged += (_, intent) =>
        {
            if (intent.State == ArkIntentState.BatchSucceeded)
                firstBatchTcs.TrySetResult(intent);
        };
        var succeededIntent = await firstBatchTcs.Task.WaitAsync(TimeSpan.FromMinutes(1));

        // ── Second redemption attempt: re-submit the same intent ───────────────────
        // Reset the succeeded intent to WaitingToSubmit so the running
        // IntentSynchronizationService will try to register it again with arkd.
        // arkd will reject it because the note's VTXO is already spent.
        var cancelledTcs = new TaskCompletionSource();
        intentStorage.IntentChanged += (_, intent) =>
        {
            if (intent.IntentTxId == succeededIntent.IntentTxId &&
                intent.State == ArkIntentState.Cancelled)
                cancelledTcs.TrySetResult();
        };

        await intentStorage.SaveIntent(succeededIntent.WalletId,
            succeededIntent with
            {
                State = ArkIntentState.WaitingToSubmit,
                IntentId = null,
                UpdatedAt = DateTimeOffset.UtcNow
            });

        // The IntentSynchronizationService picks up the reset intent and submits it.
        // arkd returns "VTXO already spent" — the service marks it Cancelled.
        await cancelledTcs.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var finalIntent = (await intentStorage.GetIntents(intentTxIds: [succeededIntent.IntentTxId]))
            .SingleOrDefault();

        Assert.That(finalIntent, Is.Not.Null);
        Assert.That(finalIntent!.State, Is.EqualTo(ArkIntentState.Cancelled));
        Assert.That(finalIntent.CancellationReason, Does.Contain("VTXO").IgnoreCase,
            "The cancellation reason should reference the already-spent VTXO");

        await arkHost.StopAsync();
    }

    /// <summary>
    /// Two independent Arkade hosts both try to redeem the same note (simulating two different
    /// users who received the same bearer note). The note's VTXO can only be spent once:
    /// exactly one host reaches BatchSucceeded and arkd rejects the other (Cancelled).
    /// Each host has isolated storage and services
    /// </summary>
    // TODO: AlreadyLockedVtxoException recovery uses a VTXO-key-based DeleteProof, so host B
    // evicts host A's registration and the evicted intent hangs in WaitingForBatch forever.
    // Fix requires either arkd returning the lock owner or changing the recovery to not evict competitors.
    // should be done on W3 along with Concurrent settle from 2 wallets test
    [Test, Ignore("Bug: concurrent note redemption — evicted intent hangs in WaitingForBatch, see TODO above")]
    public async Task SameNote_ConcurrentRedemption_ExactlyOneSucceeds()
    {
        var note = await DockerHelper.CreateArkNote();
        if (string.IsNullOrEmpty(note)) throw new Exception("Note creation failed!");

        IHost BuildArkHost() =>
            Host.CreateDefaultBuilder([])
                .AddArk()
                .OnCustomGrpcArk(SharedArkInfrastructure.ArkdEndpoint.ToString())
                .WithSafetyService<AsyncSafetyService>()
                .WithIntentScheduler<SimpleIntentScheduler>()
                .WithWalletProvider<InMemoryWalletProvider>()
                .ConfigureServices((_, s) =>
                {
                    s.AddDbContextFactory<TestDbContext>(options =>
                        options.UseInMemoryDatabase($"Test_{Guid.NewGuid():N}"));
                    s.AddArkEfCoreStorage<TestDbContext>();
                    s.AddNBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
                })
                .ConfigureServices(s => s.Configure<SimpleIntentSchedulerOptions>(o =>
                {
                    o.Threshold = TimeSpan.FromHours(2);
                    o.ThresholdHeight = 2000;
                }))
                .ConfigureServices(s => s.Configure<IntentGenerationServiceOptions>(
                    o => o.PollInterval = TimeSpan.FromSeconds(5)))
                .Build();

        var host1 = BuildArkHost();
        var host2 = BuildArkHost();

        var contractService1 = host1.Services.GetRequiredService<IContractService>();
        var contractService2 = host2.Services.GetRequiredService<IContractService>();
        var wallet1 = host1.Services.GetRequiredService<InMemoryWalletProvider>();
        var wallet2 = host2.Services.GetRequiredService<InMemoryWalletProvider>();
        var intentStorage1 = host1.Services.GetRequiredService<IIntentStorage>();
        var intentStorage2 = host2.Services.GetRequiredService<IIntentStorage>();

        var fp1 = await wallet1.CreateTestWallet();
        var fp2 = await wallet2.CreateTestWallet();
        await contractService1.ImportContract(fp1, ArkNoteContract.Parse(note));
        await contractService2.ImportContract(fp2, ArkNoteContract.Parse(note));

        var winnerTcs = new TaskCompletionSource<ArkIntent>();
        var loserTcs = new TaskCompletionSource<ArkIntent>();

        void Trace(string host, ArkIntent intent)
        {
            TestContext.Out.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] {host} → {intent.State}" +
                (intent.CancellationReason is not null ? $" ({intent.CancellationReason})" : "") +
                (intent.BatchId is not null ? $" batch={intent.BatchId[..8]}" : "") +
                (intent.IntentId is not null ? $" intentId={intent.IntentId[..8]}" : ""));
            if (intent.State == ArkIntentState.BatchSucceeded) winnerTcs.TrySetResult(intent);
            else if (intent.State == ArkIntentState.Cancelled) loserTcs.TrySetResult(intent);
        }

        intentStorage1.IntentChanged += (_, intent) => Trace("host1", intent);
        intentStorage2.IntentChanged += (_, intent) => Trace("host2", intent);

        await Task.WhenAll(host1.StartAsync(), host2.StartAsync());

        await Task.WhenAll(
            winnerTcs.Task.WaitAsync(TimeSpan.FromMinutes(2)),
            loserTcs.Task.WaitAsync(TimeSpan.FromMinutes(2)));

        var winner = winnerTcs.Task.Result;
        var loser = loserTcs.Task.Result;

        Assert.That(winner.State, Is.EqualTo(ArkIntentState.BatchSucceeded));
        Assert.That(loser.State, Is.EqualTo(ArkIntentState.Cancelled));
        Assert.That(loser.CancellationReason, Does.Contain("VTXO").IgnoreCase,
            "Cancellation must originate from arkd rejecting the duplicate VTXO registration");

        await Task.WhenAll(host1.StopAsync(), host2.StopAsync());
    }
}
