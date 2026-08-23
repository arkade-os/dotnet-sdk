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
/// End-to-end proof that <see cref="NArk.Core.Sweeper.ServerKeyRotationSweepPolicy"/> migrates funds
/// sitting under a <b>deprecated</b> arkd signer back to the <b>current</b> signer.
/// <para>
/// Creating a genuine deprecated-signer VTXO needs no mid-test rotation: an Arkade address encodes the
/// taproot output key and the server key as independent fields, and <see cref="ArkTxOut"/> keeps only the
/// scriptPubKey — so spending to a deprecated-signer contract's address lands a real VTXO at that
/// (deprecated) script while arkd only ever sees a plain taproot output. The wallet is self-funded via an
/// arkd note (no Fulmine dependency). Requires the stack to advertise a within-cutoff deprecated signer
/// (env <c>ARKD_WALLET_DEPRECATED_SIGNER_KEYS</c>); skips cleanly otherwise.
/// </para>
/// </summary>
public class ServerKeyRotationSweeperTests
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
                // VtxosChanged trigger firing for the injected deprecated-signer coin.
                s.Configure<SweeperServiceOptions>(o => o.ForceRefreshInterval = TimeSpan.FromSeconds(5));
            })
            .Build();

    [Test]
    public async Task DeprecatedSignerVtxo_IsAutomaticallySweptToCurrentSigner()
    {
        using var host = BuildHost($"RotationSweep_{Guid.NewGuid():N}");
        await host.StartAsync();

        var transport = host.Services.GetRequiredService<IClientTransport>();
        var info = await transport.GetServerInfoAsync();

        // Requires a within-cutoff deprecated signer (ARKD_WALLET_DEPRECATED_SIGNER_KEYS).
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var deprecated = info.DeprecatedSigners.FirstOrDefault(kvp => kvp.Value == 0 || kvp.Value > now);
        if (deprecated.Key is null)
            Assert.Ignore("Stack advertises no within-cutoff deprecated signer; " +
                          "set ARKD_WALLET_DEPRECATED_SIGNER_KEYS to run this test.");

        var currentServerKey = info.SignerKey.ToXOnlyPubKey();
        var deprecatedServerKey = deprecated.Key;
        Assert.That(deprecatedServerKey.ToBytes(), Is.Not.EqualTo(currentServerKey.ToBytes()),
            "the deprecated signer must differ from the current signer");

        var walletStorage = host.Services.GetRequiredService<IWalletStorage>();
        var contractService = host.Services.GetRequiredService<IContractService>();
        var contractStorage = host.Services.GetRequiredService<IContractStorage>();
        var vtxoStorage = host.Services.GetRequiredService<IVtxoStorage>();
        var intentStorage = host.Services.GetRequiredService<IIntentStorage>();
        var walletProvider = host.Services.GetRequiredService<IWalletProvider>();
        var spendingService = host.Services.GetRequiredService<ISpendingService>();

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

        // ── Persist a contract UNDER THE DEPRECATED SIGNER ──────────────────────
        // ContractService.ImportContract rejects non-current server keys, so save the entity directly —
        // exactly the state a pre-rotation contract is left in once the signer rotates.
        var userSigner = await (await walletProvider.GetAddressProviderAsync(walletId))!.GetNextSigningDescriptor();
        var deprecatedContract = new ArkPaymentContract(
            deprecatedServerKey.ToOutputDescriptor(Network.RegTest),
            info.UnilateralExit,
            userSigner);
        var deprecatedScript = deprecatedContract.GetArkAddress().ScriptPubKey.ToHex();
        await contractStorage.SaveContract(deprecatedContract.ToEntity(
            walletId, defaultServerKey: deprecatedServerKey.ToOutputDescriptor(Network.RegTest)));

        // ── Move funds onto the deprecated-signer script ────────────────────────
        // ArkTxOut keeps only the scriptPubKey, so arkd creates the VTXO at the genuine deprecated-signer
        // script P2TR(K_deprecated); the address's server key never travels to arkd.
        await spendingService.Spend(walletId,
            [new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(500_000), deprecatedContract.GetArkAddress())]);

        // The deprecated-signer VTXO must materialise (proves funding the deprecated script worked).
        await WaitUntilAsync(async () =>
                (await vtxoStorage.GetVtxos(scripts: [deprecatedScript], includeSpent: true)).Count > 0,
            TimeSpan.FromSeconds(45),
            "deprecated-signer VTXO never appeared — could not fund the deprecated script");

        // ── The hosted SweeperService migrates it to the current signer ─────────
        // Definitive sweep signal: the deprecated-signer VTXO gets spent (only the sweeper touches it).
        await WaitUntilAsync(async () =>
                (await vtxoStorage.GetVtxos(scripts: [deprecatedScript], includeSpent: true)).Any(v => v.IsSpent()),
            TimeSpan.FromSeconds(90),
            "sweeper did not spend the deprecated-signer VTXO");

        // Migrated funds must now be spendable under the CURRENT signer, with nothing left under the deprecated one.
        var coinsAfter = await spendingService.GetAvailableCoins(walletId);
        Assert.Multiple(() =>
        {
            Assert.That(coinsAfter.Any(c => IsUnder(c, currentServerKey)), Is.True,
                "swept funds must land under the current signer");
            Assert.That(coinsAfter.Any(c => IsUnder(c, deprecatedServerKey)), Is.False,
                "no spendable funds should remain under the deprecated signer");
        });

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
