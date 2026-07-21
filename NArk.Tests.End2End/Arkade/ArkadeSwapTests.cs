using Microsoft.Extensions.Options;
using NArk.Abstractions.VTXOs;
using NArk.Arkade.Contracts;
using NArk.Arkade.Emulator;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Services;
using NArk.Core.Assets;
using NArk.Core.Services;
using NArk.Core.Transformers;
using NArk.Core.Transport;
using NArk.Tests.End2End.Common;
using NArk.Tests.End2End.TestPersistance;

namespace NArk.Tests.End2End.Arkade;

/// <summary>
/// End-to-end tests for the maker side of Arkade non-interactive swaps (against a live arkd +
/// emulator, docker <c>--profile emulator</c>). Covers what the SDK controls without a solver:
/// <see cref="ArkadeIntentManager.CreateSwap"/> (fund the covenant + attach the offer packet) and
/// <see cref="ArkadeIntentManager.CancelSwap"/> (reclaim the deposit via the covenant's cancel path).
/// The fulfill path is the solver's job (a separate service) and needs a live solver to exercise.
/// </summary>
[TestFixture]
public class ArkadeSwapTests
{
    private static readonly Uri EmulatorEndpoint = new("http://localhost:7073");
    private static readonly Uri SolverEndpoint = new("http://localhost:7091");
    
    private const long DepositSats = 50_000;
    private const long WantAmount = 1_000_000; // asset units the maker wants (synthetic asset)

    [Test]
    public async Task CreateSwap_FundsCovenant_AndStoresPending()
    {
        var ctx = await SetUpAsync();

        var intent = await ctx.Manager.CreateSwap(
            new CreateSwapRequest(ctx.WalletId, ArkadeSwapIntentType.BtcToAsset, DepositSats, WantAmount, SyntheticAsset()));

        Assert.That(intent.Status, Is.EqualTo(ArkadeSwapIntentStatus.Pending));
        Assert.That(intent.SwapPkScript, Is.Not.Empty);
        Assert.That(intent.OfferHex, Is.Not.Empty);

        // The persisted intent is the storage's active-scripts entry.
        var stored = (await ctx.IntentStorage.GetArkadeSwapIntents()).Single();
        Assert.That(stored.Id, Is.EqualTo(intent.Id));

        // The deposit landed in a covenant VTXO at the swap address.
        var vtxo = await WaitForUnspentVtxo(ctx, intent.SwapPkScript);
        Assert.That(vtxo.Amount, Is.EqualTo((ulong)DepositSats));
    }

    [Test]
    public async Task CreateThenCancel_ReclaimsDeposit_AndMarksCancelled()
    {
        var ctx = await SetUpAsync();

        var intent = await ctx.Manager.CreateSwap(
            new CreateSwapRequest(ctx.WalletId, ArkadeSwapIntentType.BtcToAsset, DepositSats, WantAmount, SyntheticAsset()));
        var covenantVtxo = await WaitForUnspentVtxo(ctx, intent.SwapPkScript);

        // Stand in for VtxoSynchronizationService: in a running app the intent storage is an
        // IActiveScriptsProvider, so the sync service watches the swap script and lands its VTXO in
        // storage — which is where CancelSwap reads it from.
        await ctx.VtxoStorage.UpsertVtxo(covenantVtxo);

        var cancelled = await ctx.Manager.CancelSwap(intent.Id);

        Assert.That(cancelled.Status, Is.EqualTo(ArkadeSwapIntentStatus.Cancelled));

        // The covenant VTXO is now spent (the cancel tx consumed it).
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        var spent = false;
        while (DateTimeOffset.UtcNow < deadline && !spent)
        {
            spent = true;
            await foreach (var v in ctx.Transport.GetVtxoByScriptsAsSnapshot(new HashSet<string> { intent.SwapPkScript }))
            {
                if (!v.Swept && string.IsNullOrEmpty(v.SpentByTransactionId)) spent = false;
            }
            if (!spent) await Task.Delay(1000);
        }
        Assert.That(spent, Is.True, "covenant VTXO should be spent by the cancel tx");
    }

    /// <summary>
    /// Full round-trip against the live regtest solver: the maker funds a BTC→asset offer and the
    /// dockerized solver fulfills it, paying the wanted asset to the maker's payout address. Requires
    /// the regtest <c>solver</c> profile + <c>solver-init</c> (mock pricefeed + seeded asset/market);
    /// self-<c>Ignore</c>s when they're not up. Mirrors <c>solver/test/e2e/banco_test.go::TestBancoBTCToAsset</c>.
    /// </summary>
    [Test]
    public async Task FullSwap_SolverFulfills_BtcToAsset()
    {
        var solver = new SolverClient(SolverEndpoint);
        if (!await Poll(() => solver.IsRunningAsync(), TimeSpan.FromSeconds(20)))
            Assert.Ignore("solver not running — enable the regtest `solver` profile + `solver-init`");

        var pair = (await solver.ListPairsAsync())
            .FirstOrDefault(p => p.Pair.StartsWith("BTC/", StringComparison.OrdinalIgnoreCase));
        if (pair is null)
            Assert.Ignore("no BTC/<asset> market registered by solver-init");

        var assetIdHex = pair.Pair.Split('/')[1];

        // The solver must hold the asset to pay it out.
        if (!await Poll(async () => (await solver.GetAssetBalancesAsync()).GetValueOrDefault(assetIdHex) > 0,
                TimeSpan.FromSeconds(30)))
            Assert.Ignore("solver has no asset inventory for the pair");

        var ctx = await SetUpAsync();
        var deposit = (long)Math.Clamp((ulong)DepositSats, pair.MinAmount, Math.Min(pair.MaxAmount, 200_000UL));

        // The solver rejects any offer whose price deviates more than the pair's slippage
        // (default ±1%) from the feed, so we must ask for the fair amount — not "very little"
        // (the old lowWant=10 was ~100% below feed and always rejected). The solver-init mock
        // market is configured 1 sat ↔ 1 asset unit (base_decimals=8, quote_decimals=0,
        // feed btc-asset=1e-8 BTC/unit), so the atomic quote-per-base price is exactly 1. Use
        // the canonical maker formula (SolverDiscoveryService.ComputeWantAmount ≙ discovery-client
        // computeWantAmount): conceding the default safety_bps lands the offer inside the band.
        const decimal atomicPrice = 1m; // quote-atomic per base-atomic for the 1:1 mock market
        var want = SolverDiscoveryService.ComputeWantAmount(deposit, atomicPrice, feeBps: 0);

        var intent = await ctx.Manager.CreateSwap(new CreateSwapRequest(
            ctx.WalletId, ArkadeSwapIntentType.BtcToAsset, deposit, want, AssetId.FromString(assetIdHex)));

        // The covenant forces the fill to pay the asset to the maker's payout script.
        var makerScript = Convert.ToHexString(
            OfferCodec.Decode(Convert.FromHexString(intent.OfferHex)).MakerPkScript).ToLowerInvariant();

        var filled = await Poll(() => HasAssetVtxo(ctx, makerScript, assetIdHex), TimeSpan.FromSeconds(90));
        Assert.That(filled, Is.True, "solver should fulfill the offer — asset VTXO at the maker's address");
    }

    /// <summary>
    /// The full swap in a production-shaped setup (see the class remarks on the monitor + sync wiring),
    /// asserting on the stored intent status. Provisions its <em>own</em> well-funded market via
    /// <see cref="SolverLiquidityHelper"/> so it neither depends on nor drains the shared solver-init
    /// inventory that <see cref="FullSwap_SolverFulfills_BtcToAsset"/> uses.
    /// </summary>
    [Test]
    public async Task FullSwap_ThroughMonitor_TransitionsIntentToFulfilled()
    {
        var solver = new SolverClient(SolverEndpoint);
        if (!await Poll(() => solver.IsRunningAsync(), TimeSpan.FromSeconds(20)))
            Assert.Ignore("solver not running — enable the regtest `solver` profile + `solver-init`");

        // Mint + fund our own asset market so a single 49_750-unit fill doesn't have to share the
        // stingy SOLVER_INIT_ASSET_FUNDING pool with the other solver tests.
        var assetIdHex = await SolverLiquidityHelper.EnsureAssetMarket(SolverEndpoint);
        var pair = (await solver.ListPairsAsync())
            .First(p => p.Pair.Equals($"BTC/{assetIdHex}", StringComparison.OrdinalIgnoreCase));

        var ctx = await SetUpAsync();

        // Wire the monitor + a sync that watches the pending-swap covenant scripts — the same shape as
        // AddArkadeIntentsServices: IArkadeIntentStorage is the IActiveScriptsProvider the shared
        // VtxoSynchronizationService consumes, and the monitor reacts to IVtxoStorage.VtxosChanged.
        await using var swapSync = new VtxoSynchronizationService(ctx.VtxoStorage, ctx.Transport, [ctx.IntentStorage]);
        await swapSync.StartAsync(default);
        var monitor = new ArkadeSwapIntentMonitoringService(ctx.VtxoStorage, ctx.IntentStorage);
        await monitor.StartAsync(default);

        try
        {
            var deposit = (long)Math.Clamp((ulong)DepositSats, pair.MinAmount, Math.Min(pair.MaxAmount, 200_000UL));
            // 1 sat ↔ 1 asset unit mock market → atomic price 1; the canonical maker formula lands the
            // offer inside the solver's slippage band (see FullSwap_SolverFulfills_BtcToAsset).
            var want = SolverDiscoveryService.ComputeWantAmount(deposit, price: 1m, feeBps: 0);

            var intent = await ctx.Manager.CreateSwap(new CreateSwapRequest(
                ctx.WalletId, ArkadeSwapIntentType.BtcToAsset, deposit, want, AssetId.FromString(assetIdHex)));

            var fulfilled = await Poll(async () =>
                    (await ctx.IntentStorage.GetArkadeSwapIntents())
                        .FirstOrDefault(s => s.Id == intent.Id)?.Status == ArkadeSwapIntentStatus.Fulfilled,
                TimeSpan.FromSeconds(90));

            Assert.That(fulfilled, Is.True,
                "the monitor should transition the intent to Fulfilled once the solver spends the covenant VTXO");
        }
        finally
        {
            await monitor.StopAsync(default);
        }
    }

    /// <summary>
    /// Cancel in the same production-shaped setup, proving the monitor does <b>not</b> misread a cancel
    /// spend as a fill. <see cref="ArkadeIntentManager.CancelSwap"/> moves the intent out of
    /// <see cref="ArkadeSwapIntentStatus.Pending"/> before spending the covenant's cancel path, and
    /// <see cref="IArkadeIntentStorage.UpdateStatus"/> only transitions pending swaps — so the monitor's
    /// reaction to the (now spent) covenant VTXO is a no-op and the intent stays
    /// <see cref="ArkadeSwapIntentStatus.Cancelled"/>. Self-contained: a synthetic asset the solver never
    /// touches, cancelled by us.
    /// </summary>
    [Test]
    public async Task CancelThroughMonitor_MarksCancelled_AndIsNotMisreadAsFill()
    {
        var ctx = await SetUpAsync();

        await using var swapSync = new VtxoSynchronizationService(ctx.VtxoStorage, ctx.Transport, [ctx.IntentStorage]);
        await swapSync.StartAsync(default);
        var monitor = new ArkadeSwapIntentMonitoringService(ctx.VtxoStorage, ctx.IntentStorage);
        await monitor.StartAsync(default);

        try
        {
            var intent = await ctx.Manager.CreateSwap(new CreateSwapRequest(
                ctx.WalletId, ArkadeSwapIntentType.BtcToAsset, DepositSats, WantAmount, SyntheticAsset()));

            // The real sync lands the covenant VTXO in storage (it watches the pending-swap script), so
            // CancelSwap can read it — no manual UpsertVtxo stand-in like CreateThenCancel needs.
            var landed = await Poll(async () =>
                    (await ctx.VtxoStorage.GetVtxos(scripts: [intent.SwapPkScript])).Count > 0,
                TimeSpan.FromSeconds(30));
            Assert.That(landed, Is.True, "sync should land the covenant VTXO in storage");

            var cancelled = await ctx.Manager.CancelSwap(intent.Id);
            Assert.That(cancelled.Status, Is.EqualTo(ArkadeSwapIntentStatus.Cancelled));

            // Give the monitor a beat to observe the cancel spend, then confirm it did not flip the
            // already-cancelled intent to Fulfilled.
            await Task.Delay(2000);
            var stored = (await ctx.IntentStorage.GetArkadeSwapIntents()).First(s => s.Id == intent.Id);
            Assert.That(stored.Status, Is.EqualTo(ArkadeSwapIntentStatus.Cancelled),
                "the cancel spend must not be misread by the monitor as a fill");
        }
        finally
        {
            await monitor.StopAsync(default);
        }
    }

    /// <summary>
    /// The reverse (asset→BTC) leg, in the same production-shaped setup. The test wallet is funded only
    /// with BTC, so it first acquires the asset via a BTC→asset fill, then deposits that asset for an
    /// asset→BTC swap. <see cref="SolverLiquidityHelper.EnsureAssetMarket"/> registers <em>both</em>
    /// directions and seeds the solver's asset inventory; the reverse covenant
    /// (<c>ArkadeIntentPrograms.AssetToBtc</c>: output 0 pays ≥ <c>$wantAmount</c> to <c>$makerWP</c>)
    /// forces the solver to pay BTC, which it holds from solver-init plus leg 1's deposit.
    /// </summary>
    [Test]
    public async Task FullSwap_AssetToBtc_ThroughMonitor_TransitionsIntentToFulfilled()
    {
        var solver = new SolverClient(SolverEndpoint);
        if (!await Poll(() => solver.IsRunningAsync(), TimeSpan.FromSeconds(20)))
            Assert.Ignore("solver not running — enable the regtest `solver` profile + `solver-init`");

        // Our own funded market: this also registers the <asset>/BTC reverse pair (EnsureAssetMarket
        // registers both directions) and seeds the solver's asset inventory for leg 1.
        var assetIdHex = await SolverLiquidityHelper.EnsureAssetMarket(SolverEndpoint);
        var pairs = await solver.ListPairsAsync();
        var btcToAsset = pairs.First(p => p.Pair.Equals($"BTC/{assetIdHex}", StringComparison.OrdinalIgnoreCase));

        var ctx = await SetUpAsync();
        await using var swapSync = new VtxoSynchronizationService(ctx.VtxoStorage, ctx.Transport, [ctx.IntentStorage]);
        await swapSync.StartAsync(default);
        var monitor = new ArkadeSwapIntentMonitoringService(ctx.VtxoStorage, ctx.IntentStorage);
        await monitor.StartAsync(default);

        try
        {
            var asset = AssetId.FromString(assetIdHex);

            // Leg 1 — acquire the asset: a BTC→asset swap the solver fills, paying the asset to our maker script.
            var deposit1 = (long)Math.Clamp((ulong)DepositSats, btcToAsset.MinAmount, Math.Min(btcToAsset.MaxAmount, 200_000UL));
            var want1 = SolverDiscoveryService.ComputeWantAmount(deposit1, price: 1m, feeBps: 0);
            var leg1 = await ctx.Manager.CreateSwap(new CreateSwapRequest(
                ctx.WalletId, ArkadeSwapIntentType.BtcToAsset, deposit1, want1, asset));
            var maker1 = Convert.ToHexString(
                OfferCodec.Decode(Convert.FromHexString(leg1.OfferHex)).MakerPkScript).ToLowerInvariant();
            if (!await Poll(() => HasAssetVtxo(ctx, maker1, assetIdHex), TimeSpan.FromSeconds(90)))
                Assert.Ignore("first leg (BTC→asset) did not fill — cannot exercise the reverse");

            // Leg 2 — the reverse: deposit half the asset we now hold, want BTC back.
            var depositAsset = Math.Max(1, want1 / 2);
            var want2 = SolverDiscoveryService.ComputeWantAmount(depositAsset, price: 1m, feeBps: 0);
            var leg2 = await ctx.Manager.CreateSwap(new CreateSwapRequest(
                ctx.WalletId, ArkadeSwapIntentType.AssetToBtc, depositAsset, want2, asset));

            var fulfilled = await Poll(async () =>
                    (await ctx.IntentStorage.GetArkadeSwapIntents())
                        .FirstOrDefault(s => s.Id == leg2.Id)?.Status == ArkadeSwapIntentStatus.Fulfilled,
                TimeSpan.FromSeconds(90));

            Assert.That(fulfilled, Is.True,
                "the monitor should transition the asset→BTC intent to Fulfilled once the solver fills it");
        }
        finally
        {
            await monitor.StopAsync(default);
        }
    }

    // ─── Setup + helpers ──────────────────────────────────────────────


    private static async Task<bool> Poll(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(1000);
        }
        return false;
    }

    private static async Task<bool> HasAssetVtxo(Ctx ctx, string scriptHex, string assetId)
    {
        await foreach (var v in ctx.Transport.GetVtxoByScriptsAsSnapshot(new HashSet<string> { scriptHex }))
        {
            if (!string.IsNullOrEmpty(v.SpentByTransactionId) || v.Swept) continue;
            if (v.Assets is { Count: > 0 } assets && assets.Any(a => a.AssetId == assetId)) return true;
        }
        return false;
    }

    private sealed record Ctx(
        string WalletId,
        IClientTransport Transport,
        IVtxoStorage VtxoStorage,
        InMemoryIntentStorage IntentStorage,
        ArkadeIntentManager Manager);

    private static async Task<Ctx> SetUpAsync()
    {
        var w = await FundedWalletHelper.GetFundedWallet();

        var emulator = new EmulatorClient(new HttpClient(),
            Options.Create(new EmulatorClientOptions { ServerUrl = EmulatorEndpoint.ToString() }));

        // PaymentContractTransformer spends the wallet's own funded coins (the deposit + cancel
        // change); ArkProgramContractTransformer spends the covenant's cancel path.
        var coinService = new CoinService(w.clientTransport, w.contracts,
            [new PaymentContractTransformer(w.walletProvider), new ArkProgramContractTransformer(w.walletProvider)]);

        var spendingService = new SpendingService(
            w.vtxoStorage, w.contracts, coinService, w.walletProvider, w.contractService, w.clientTransport,
            new NArk.Core.CoinSelector.DefaultCoinSelector(), w.safetyService, TestStorage.CreateIntentStorage(),
            postSpendEventHandlers: [], logger: null,
            extensionPacketProviders: [new ArkadeEmulatorPacketProvider()],
            submitHandlers: [new ArkadeEmulatorSpendSubmitter(emulator)]);

        var intentStorage = new InMemoryIntentStorage();
        var manager = new ArkadeIntentManager(
            w.clientTransport, emulator, w.contractService, w.walletProvider, spendingService, intentStorage,
            w.vtxoStorage);

        return new Ctx(w.walletIdentifier, w.clientTransport, w.vtxoStorage, intentStorage, manager);
    }

    private static AssetId SyntheticAsset() =>
        AssetId.Create(Convert.ToHexString(Enumerable.Repeat((byte)0xab, 32).ToArray()), 0);

    private static async Task<ArkVtxo> WaitForUnspentVtxo(Ctx ctx, string scriptHex)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await foreach (var vtxo in ctx.Transport.GetVtxoByScriptsAsSnapshot(new HashSet<string> { scriptHex }))
            {
                if (!vtxo.Swept && string.IsNullOrEmpty(vtxo.SpentByTransactionId))
                    return vtxo;
            }
            await Task.Delay(1000);
        }
        throw new TimeoutException($"No spendable VTXO appeared at {scriptHex} within 30s.");
    }

    /// <summary>In-memory <see cref="IArkadeIntentStorage"/> for the test — the real one is EF Core.</summary>
    private sealed class InMemoryIntentStorage : IArkadeIntentStorage
    {
        private readonly Dictionary<string, ArkadeSwapIntent> _byId = new();

        public event EventHandler<ArkadeSwapIntent>? SwapsChanged;
        public event EventHandler? ActiveScriptsChanged;

        public Task<IReadOnlyCollection<ArkadeSwapIntent>> GetArkadeSwapIntents(
            ArkadeSwapIntentStatus? status = null,
            string? swapPkScript = null,
            string[]? walletIds = null,
            int? skip = null,
            int? take = null,
            CancellationToken cancellationToken = default)
        {
            var query = _byId.Values.AsEnumerable();
            if (status is { } s) query = query.Where(x => x.Status == s);
            if (swapPkScript is not null) query = query.Where(x => x.SwapPkScript == swapPkScript);
            if (walletIds is not null) query = query.Where(x => walletIds.Contains(x.WalletId));
            return Task.FromResult<IReadOnlyCollection<ArkadeSwapIntent>>(query.ToList());
        }

        public Task SaveArkadeSwapIntent(ArkadeSwapIntent intent, CancellationToken cancellationToken = default)
        {
            _byId[intent.Id] = intent;
            SwapsChanged?.Invoke(this, intent);
            ActiveScriptsChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task<bool> UpdateStatus(string swapPkScript, ArkadeSwapIntentStatus status, string? spentTxid = null,
            CancellationToken cancellationToken = default)
        {
            var e = _byId.Values.FirstOrDefault(x => x.SwapPkScript == swapPkScript && x.Status == ArkadeSwapIntentStatus.Pending);
            if (e is null) return Task.FromResult(false);
            e.Status = status;
            if (spentTxid is not null) e.SpentTxid = spentTxid;
            SwapsChanged?.Invoke(this, e);
            return Task.FromResult(true);
        }
    }
}
