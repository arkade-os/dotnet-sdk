using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NArk.Abstractions;
using NArk.Core;
using NArk.Core.Contracts;
using NArk.Core.Models.Options;
using NArk.Core.Services;
using NArk.Hosting;
using NArk.Safety.AsyncKeyedLock;
using NArk.Storage.EfCore.Hosting;
using NArk.Tests.Common;
using NArk.Tests.End2End.Core;
using NArk.Tests.End2End.TestPersistance;
using NBitcoin;

namespace NArk.Tests.End2End.Common;

/// <summary>
/// Funds arbitrary Arkade addresses on the regtest stack. This is the single funding
/// entry point for the E2E suite — call <see cref="Fund"/> whenever a test needs spendable
/// VTXOs at an address it controls.
/// </summary>
/// <remarks>
/// <para>
/// The faucet is an Arkade host of our own, built on this SDK and started once per test
/// session. It owns an in-memory wallet, mints its own balance from server-issued notes
/// (<c>arkd note</c>), and serves each request as an ordinary offchain spend — so funding a
/// test costs no batch and adds no measurable time to it.
/// </para>
/// <para>
/// It is a service rather than a CLI call because the stack expires VTXOs aggressively (a
/// VTXO lives ~69s here, which is deliberate — it is what the expiry suite exercises). A
/// long-lived host runs <see cref="SimpleIntentScheduler"/>, which renews coins as they
/// approach expiry, so the faucet's balance stays continuously spendable instead of decaying
/// between tests.
/// </para>
/// <para>
/// This replaced funding through the in-container <c>ark</c> CLI wallet, which could not keep
/// itself alive: that client skips forfeits for coins it considers recoverable
/// (<c>Swept || expired</c>) while arkd demands a forfeit for every coin that is merely
/// <c>!Swept &amp;&amp; !Unrolled</c>. An expired-but-unswept coin therefore lands the wallet in a
/// permanent <c>missing forfeit transactions</c> ban. This SDK forfeits on the server's own
/// predicate (<see cref="ArkCoin.RequiresForfeit"/>), so the faucet is not exposed to that
/// mismatch even if renewal falls behind. Funding before that ran through Fulmine, which is
/// still in the stack — but only as the <c>delegate</c> profile's delegator, the one role the
/// delegation suite genuinely needs it for.
/// </para>
/// </remarks>
public static class ArkadeFaucet
{
    /// <summary>Sats minted per note when the faucet tops itself up.</summary>
    private const long NoteSats = 10_000_000;

    /// <summary>
    /// How many notes the faucet may mint for one request before giving up. A request larger
    /// than this is a test bug, not a funding shortfall.
    /// </summary>
    private const int MaxTopUps = 4;

    /// <summary>
    /// Renew a coin once it is this close to expiry. A batch session is 30s and a VTXO lives
    /// ~69s here, so 45s leaves renewal a full session plus margin to land.
    /// </summary>
    private static readonly TimeSpan RenewalThreshold = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Refuse to spend a coin this close to expiry. arkd rejects an offchain spend of a coin it
    /// already considers recoverable, and a coin can cross that line between selection and submit.
    /// </summary>
    private static readonly TimeSpan ExpirySafetyWindow = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Attempts per request. Renewal runs continuously against a ~69s VTXO lifetime, so at any
    /// moment some of the faucet's coins are locked in an in-flight intent — a collision is normal
    /// and transient, not a failure.
    /// </summary>
    private const int MaxSendAttempts = 6;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>Deadline for a top-up to settle and show up as spendable.</summary>
    private static readonly TimeSpan TopUpTimeout = TimeSpan.FromMinutes(3);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Task<Faucet>? _faucet;

    /// <summary>
    /// Sends <paramref name="amountSats"/> to <paramref name="arkadeAddress"/> offchain and
    /// returns the Arkade transaction id. Starts the faucet host on first use and tops its
    /// balance up from a note when the request exceeds what it currently holds.
    /// </summary>
    /// <param name="arkadeAddress">Bech32m Arkade address to credit.</param>
    /// <param name="amountSats">Amount to send, in satoshis.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<string> Fund(string arkadeAddress, long amountSats, CancellationToken ct = default)
    {
        if (amountSats <= 0) throw new ArgumentOutOfRangeException(nameof(amountSats), "must be > 0");
        if (!ArkAddress.TryParse(arkadeAddress, out var destination) || destination is null)
            throw new ArgumentException($"Not a valid Arkade address: '{arkadeAddress}'", nameof(arkadeAddress));

        var faucet = await Instance(ct);

        // Serialize the whole request: coin selection and the spend that consumes it have to be
        // one step, or two concurrent tests select the same coins and one of them loses a race
        // it never knew it was in.
        await Gate.WaitAsync(ct);
        try
        {
            var txid = await faucet.Send(destination, amountSats, ct);
            return txid.ToString();
        }
        finally
        {
            Gate.Release();
        }
    }

    private static Task<Faucet> Instance(CancellationToken ct)
    {
        // Double-checked so the host is built once per session; the build itself is awaited by
        // every caller that arrives while it is still starting.
        if (_faucet is { } running) return running;
        lock (Gate)
        {
            return _faucet ??= Faucet.Start(ct);
        }
    }

    private sealed class Faucet(string walletId, IContractService contracts, ISpendingService spending)
    {
        public string WalletId { get; } = walletId;
        public ISpendingService Spending { get; } = spending;

        public static async Task<Faucet> Start(CancellationToken ct)
        {
            var host = Host.CreateDefaultBuilder([])
                .AddArk()
                .OnCustomGrpcArk(SharedArkInfrastructure.ArkdEndpoint.ToString())
                .WithSafetyService<AsyncSafetyService>()
                .WithIntentScheduler<SimpleIntentScheduler>()
                .WithWalletProvider<InMemoryWalletProvider>()
                .ConfigureServices((_, s) =>
                {
                    s.AddDbContextFactory<TestDbContext>(options =>
                        options.UseInMemoryDatabase($"Faucet_{Guid.NewGuid():N}"));
                    s.AddArkEfCoreStorage<TestDbContext>();
                    s.AddNBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
                })
                .ConfigureServices(s => s.Configure<SimpleIntentSchedulerOptions>(o =>
                {
                    o.Threshold = RenewalThreshold;
                    // Expiry is also height-bounded on this stack, and regtest mines fast enough
                    // that the height bound can bite first.
                    o.ThresholdHeight = 60;
                }))
                .ConfigureServices(s => s.Configure<IntentGenerationServiceOptions>(
                    o => o.PollInterval = TimeSpan.FromSeconds(5)))
                .Build();

            await host.StartAsync(ct);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => host.StopAsync().GetAwaiter().GetResult();

            var wallets = host.Services.GetRequiredService<InMemoryWalletProvider>();
            var walletId = await wallets.CreateTestWallet();

            var faucet = new Faucet(

                walletId,
                host.Services.GetRequiredService<IContractService>(),
                host.Services.GetRequiredService<ISpendingService>());

            await faucet.TopUp(ct);
            return faucet;
        }

        /// <summary>
        /// Sends <paramref name="amountSats"/> to <paramref name="destination"/>, minting notes when
        /// the balance is short and retrying when a coin is unusable for a reason that passes.
        /// </summary>
        /// <remarks>
        /// Coins are chosen here rather than left to automatic selection because two of this stack's
        /// states are invisible to it: a coin locked by an in-flight renewal intent, and a coin close
        /// enough to expiry that arkd will call it recoverable and refuse the spend. Both are normal
        /// under a ~69s VTXO lifetime with renewal always running, so both are retried rather than
        /// surfaced — by then the lock has cleared or renewal has produced a fresh coin.
        /// </remarks>
        public async Task<uint256> Send(IDestination destination, long amountSats, CancellationToken ct)
        {
            // The spend pays an input fee, so cover the request with headroom rather than exactly.
            var needed = amountSats + Math.Max(amountSats / 10, 10_000);
            ArkTxOut[] outputs = [new(ArkTxOutType.Vtxo, Money.Satoshis(amountSats), destination)];
            var topUps = 0;

            for (var attempt = 1; attempt <= MaxSendAttempts; attempt++)
            {
                var coins = await PickCoins(needed, ct);
                if (coins is null)
                {
                    if (topUps++ >= MaxTopUps)
                        throw new InvalidOperationException(
                            $"Faucet could not cover {needed} sats after {MaxTopUps} top-ups of {NoteSats} sats " +
                            $"(request {amountSats} sats). Either the request is larger than the faucet is " +
                            "sized for, or notes are not settling.");

                    await TopUp(ct);
                    continue;
                }

                try
                {
                    return await Spending.Spend(WalletId, coins, outputs, ct);
                }
                catch (Exception e) when (IsTransient(e) && attempt < MaxSendAttempts)
                {
                    await Task.Delay(RetryDelay, ct);
                }
            }

            throw new InvalidOperationException(
                $"Faucet could not send {amountSats} sats in {MaxSendAttempts} attempts: every attempt hit " +
                "a locked or already-recoverable coin. Renewal is not keeping ahead of VTXO expiry.");
        }

        /// <summary>
        /// A coin is unusable for a reason that passes: it is locked by an in-flight spend or
        /// renewal, or arkd has already declared it recoverable. Retrying with a fresh selection is
        /// the correct response to both.
        /// </summary>
        private static bool IsTransient(Exception e) =>
            e is AlreadyLockedVtxoException ||
            (e is RpcException rpc && rpc.Status.Detail.Contains("VTXO_RECOVERABLE", StringComparison.Ordinal));

        /// <summary>
        /// Selects coins covering <paramref name="needed"/> sats, or null when the spendable set
        /// cannot cover it and the faucet has to mint.
        /// </summary>
        private async Task<ArkCoin[]?> PickCoins(long needed, CancellationToken ct)
        {
            var usable = (await Spending.GetAvailableCoins(WalletId, ct))
                .Where(IsSpendable)
                .OrderByDescending(c => c.Amount.Satoshi)
                .ToArray();

            var picked = new List<ArkCoin>();
            var total = 0L;
            foreach (var coin in usable)
            {
                picked.Add(coin);
                total += coin.Amount.Satoshi;
                if (total >= needed) return picked.ToArray();
            }

            return null;
        }

        /// <summary>
        /// Redeems one server-issued note into the wallet and waits for it to become spendable.
        /// The note lands via a batch, which the scheduler drives; polling the spendable set is
        /// what tells us the funds are actually usable, rather than merely settled.
        /// </summary>
        private async Task TopUp(CancellationToken ct)
        {
            var before = await Balance(ct);

            var note = await DockerHelper.CreateArkNote(NoteSats, ct);
            if (string.IsNullOrEmpty(note))
                throw new InvalidOperationException("arkd refused to issue a note for the faucet");

            await contracts.ImportContract(WalletId, ArkNoteContract.Parse(note));

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TopUpTimeout);
            try
            {
                while (await Balance(deadline.Token) <= before)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), deadline.Token);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Faucet note of {NoteSats} sats did not become spendable within {TopUpTimeout.TotalSeconds:0}s " +
                    $"(balance still {await Balance(CancellationToken.None)} sats). " +
                    "The note redemption batch did not land — check arkd's batch log.");
            }
        }

        /// <summary>
        /// Sats the faucet can actually send right now. A freshly imported note shows up as an
        /// available coin immediately, but it carries no server key and so has no checkpoint
        /// contract — it is only redeemable through a batch, never through an offchain spend.
        /// Counting it would end the top-up wait early over a coin that cannot be spent at all.
        /// </summary>
        private async Task<long> Balance(CancellationToken ct)
        {
            var coins = await Spending.GetAvailableCoins(WalletId, ct);
            return coins.Where(IsSpendable).Sum(c => c.Amount.Satoshi);
        }

        /// <summary>
        /// A coin the faucet can actually send right now: it carries a server key, so it has a
        /// checkpoint contract, and it is far enough from expiry that arkd will not have declared
        /// it recoverable by the time the spend is submitted.
        /// </summary>
        private static bool IsSpendable(ArkCoin coin) =>
            coin.Contract.Server is not null &&
            (coin.ExpiresAt is not { } expiry || expiry - ExpirySafetyWindow > DateTimeOffset.UtcNow);

    }
}
