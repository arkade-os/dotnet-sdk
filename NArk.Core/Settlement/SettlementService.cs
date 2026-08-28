using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Settlement;
using NArk.Abstractions.VTXOs;
using NArk.Core.Enums;
using NArk.Core.Events;
using NArk.Core.Extensions;
using NArk.Core.Services;

namespace NArk.Core.Settlement;

/// <summary>
/// The settlement engine: watches wallet activity, asks the registered
/// <see cref="ISettlementPolicy"/> instances whether a wallet should settle, and routes
/// the resulting plan through <see cref="CompositeSettlementService"/>.
/// <para>
/// A wallet is re-evaluated when its VTXOs change, when one of its intents leaves a
/// batch, when an <see cref="ISettlementTriggerSource"/> reports activity, and on a
/// periodic heartbeat that doubles as the retry for transient failures. Evaluations are
/// debounced and deduplicated per wallet, and run one wallet at a time.
/// </para>
/// <para>
/// This is separate from <see cref="NArk.Core.Services.SweeperService"/>, which
/// consolidates VTXOs back into the same wallet. Settlement moves value out.
/// </para>
/// </summary>
public class SettlementService(
    IEnumerable<ISettlementPolicy> policies,
    IEnumerable<ISettlementGate> gates,
    IEnumerable<ISettlementTriggerSource> triggerSources,
    CompositeSettlementService settlementRouter,
    ISettlementConfigProvider configProvider,
    ISpendingService spendingService,
    IIntentStorage intentStorage,
    IVtxoStorage vtxoStorage,
    IContractStorage contractStorage,
    IBitcoinBlockchain blockchain,
    IOptions<SettlementOptions> options,
    IEnumerable<IEventHandler<PostSettlementActionEvent>> postSettlementHandlers,
    ILogger<SettlementService>? logger = null) : BackgroundService
{
    private readonly Channel<string> _walletQueue = Channel.CreateUnbounded<string>();
    private readonly ConcurrentDictionary<string, byte> _queuedWallets = new();

    // One settlement at a time per wallet. The engine only avoids over-committing a balance
    // because it executes plans against a shrinking remainder, and that bookkeeping lives
    // inside a single evaluation: a user-initiated SettleAsync running beside a background
    // pass would read the same balance and spend it twice. Entries are keyed by wallet and
    // never removed — one semaphore per wallet the process has ever settled.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _walletLocks = new();

    /// <summary>
    /// Queues a wallet for evaluation. Safe to call for any wallet — unknown and
    /// unconfigured wallets are dropped during processing, and a wallet already queued
    /// is not queued twice.
    /// </summary>
    public void QueueWallet(string walletId)
    {
        if (string.IsNullOrWhiteSpace(walletId))
            return;

        if (!_queuedWallets.TryAdd(walletId, 0))
            return;

        if (!_walletQueue.Writer.TryWrite(walletId))
            _queuedWallets.TryRemove(walletId, out _);
    }

    /// <summary>
    /// Subscribes to the wallet activity the engine reacts to, then starts the loop.
    /// <para>
    /// The subscriptions happen here rather than inside <see cref="ExecuteAsync"/> because the
    /// host schedules that method instead of running it inline: anything raised between the
    /// service starting and the loop actually running would be missed, and the wallet would
    /// wait for the next heartbeat.
    /// </para>
    /// </summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        logger?.LogInformation("Starting settlement service");

        vtxoStorage.VtxosChanged += OnVtxoChanged;
        intentStorage.IntentChanged += OnIntentChanged;
        foreach (var source in triggerSources)
            source.WalletActivity += OnTriggerSourceActivity;

        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var heartbeat = HeartbeatLoop(stoppingToken);

            while (await _walletQueue.Reader.WaitToReadAsync(stoppingToken))
            {
                while (_walletQueue.Reader.TryRead(out var walletId))
                {
                    if (options.Value.Debounce > TimeSpan.Zero)
                        await Task.Delay(options.Value.Debounce, stoppingToken);

                    // Released only once the wait is over: dropping it first would let activity
                    // arriving inside the debounce window queue the same wallet a second time,
                    // which is exactly what the window is meant to absorb.
                    _queuedWallets.TryRemove(walletId, out _);

                    await ProcessWalletSafely(walletId, stoppingToken);
                }
            }

            await heartbeat;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            // Also unsubscribed by StopAsync; doing it here as well covers the loop ending on
            // its own, and detaching a handler twice is a no-op.
            Unsubscribe();

            logger?.LogInformation("Settlement service stopped");
        }
    }

    // Wallet events drive settlement promptly; this loop is the dumb retry behind them,
    // re-queueing every configured wallet so a transiently failed or missed attempt
    // fires again without any per-wallet resume bookkeeping.
    private async Task HeartbeatLoop(CancellationToken cancellationToken)
    {
        if (options.Value.HeartbeatInterval <= TimeSpan.Zero)
            return;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                foreach (var config in await configProvider.GetConfigs(cancellationToken: cancellationToken))
                    QueueWallet(config.WalletId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to queue configured settlement wallets; retrying on the next beat");
            }

            try
            {
                await Task.Delay(options.Value.HeartbeatInterval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task ProcessWalletSafely(string walletId, CancellationToken cancellationToken)
    {
        try
        {
            await ProcessWallet(walletId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to evaluate settlement for wallet {WalletId}", walletId);
        }
    }

    private async Task ProcessWallet(string walletId, CancellationToken cancellationToken)
    {
        using var _walletScope = logger?.BeginScope(("WalletId", walletId));

        var policyList = policies.ToArray();
        if (policyList.Length == 0)
            return;

        var walletLock = LockFor(walletId);
        await walletLock.WaitAsync(cancellationToken);
        try
        {

        foreach (var gate in gates)
        {
            if (!await gate.IsBlockedAsync(walletId, cancellationToken))
                continue;

            logger?.LogDebug("Settlement for wallet {WalletId} blocked by {Gate}", walletId, gate.GetType().Name);
            return;
        }

        var context = await BuildContext(walletId, cancellationToken);
        if (context.AvailableBalanceSats <= 0 && context.AssetBalances.Count == 0)
            return;

        // The union of what every policy yields, executed in order — the same shape as
        // SweeperService running its ISweepPolicy instances. Unlike swept coins, which a
        // HashSet deduplicates, amounts do not: two policies can independently plan the
        // whole balance. Committing against a shrinking remainder is what keeps that from
        // over-spending the wallet.
        //
        // The remainder is tracked per denomination: BTC and each Arkade-issued asset are
        // separate pots, so an asset payout never eats into the satoshi balance a BTC rule
        // is waiting on, and vice versa.
        var remaining = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            [SettlementAssets.Btc] = context.AvailableBalanceSats
        };
        foreach (var assetId in context.AssetBalances.Keys)
            remaining[assetId] = context.GetAvailableBalance(assetId);

        foreach (var policy in policyList)
        {
            await foreach (var plan in policy.EvaluateAsync(context, cancellationToken))
            {
                if (plan.Amount <= 0)
                    continue;

                var remainingForAsset = remaining.GetValueOrDefault(plan.SourceAsset);
                if (plan.Amount > remainingForAsset)
                {
                    logger?.LogDebug(
                        "Skipping {Policy} plan of {Amount} {SourceAsset} for wallet {WalletId}: only {Remaining} left after earlier plans",
                        policy.GetType().Name, plan.Amount, plan.SourceAsset, walletId, remainingForAsset);
                    continue;
                }

                var request = new SettlementRequest(
                    walletId,
                    plan.Amount,
                    plan.Destination,
                    plan.SourceAsset,
                    plan.Coins,
                    plan.Reference);

                // Only a settlement that actually committed funds consumes the balance;
                // a failed one leaves the remainder for the plans behind it. The lock is
                // already held, so this goes straight to the rail.
                if (await RouteAsync(request, cancellationToken) is not null)
                    remaining[plan.SourceAsset] = remainingForAsset - plan.Amount;

                if (remaining.Values.All(amount => amount <= 0))
                    return;
            }
        }
        }
        finally
        {
            walletLock.Release();
        }
    }

    private SemaphoreSlim LockFor(string walletId) =>
        _walletLocks.GetOrAdd(walletId, _ => new SemaphoreSlim(1, 1));

    /// <inheritdoc />
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        Unsubscribe();
        return base.StopAsync(cancellationToken);
    }

    private void Unsubscribe()
    {
        vtxoStorage.VtxosChanged -= OnVtxoChanged;
        intentStorage.IntentChanged -= OnIntentChanged;
        foreach (var source in triggerSources)
            source.WalletActivity -= OnTriggerSourceActivity;
    }

    /// <summary>
    /// Executes a settlement immediately, bypassing policies and gates, and raises
    /// <see cref="PostSettlementActionEvent"/> for the outcome. Use it for a
    /// user-initiated payout that should share the engine's routing and eventing.
    /// </summary>
    public async Task<SettlementResult?> SettleAsync(
        SettlementRequest request,
        CancellationToken cancellationToken = default)
    {
        // Serialised against the background pass and against other callers for this wallet:
        // two settlements reading the same balance would both plan to spend it.
        var walletLock = LockFor(request.WalletId);
        await walletLock.WaitAsync(cancellationToken);
        try
        {
            return await RouteAsync(request, cancellationToken);
        }
        finally
        {
            walletLock.Release();
        }
    }

    // Routes and reports one settlement. The caller holds the wallet's lock.
    private async Task<SettlementResult?> RouteAsync(
        SettlementRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await settlementRouter.SettleAsync(request, cancellationToken);

            logger?.LogInformation(
                "Wallet {WalletId} settled {SourceAmount} {SourceAsset} to {Network}/{Asset}; transfer {TransferId}, expected {DestinationAmount}, fees {FeesPaidSats} sats",
                request.WalletId,
                result.SourceAmount,
                request.SourceAsset,
                request.Destination.Network,
                request.Destination.Asset,
                result.TransferId,
                result.DestinationAtomicAmount ?? result.DestinationAmountSats,
                result.FeesPaidSats);

            await postSettlementHandlers.SafeHandleEventAsync(
                new PostSettlementActionEvent(request, result, ActionState.Successful, null),
                cancellationToken: cancellationToken);

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The rail may have committed the funds before the token fired. Reporting this as a
            // failed settlement would tell the application the value is still in the wallet, so
            // the cancellation surfaces instead and the outcome stays unresolved.
            logger?.LogWarning(
                "Settlement of {Amount} {SourceAsset} for wallet {WalletId} was cancelled; whether the rail committed is unknown",
                request.Amount, request.SourceAsset, request.WalletId);
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex,
                "Failed to settle {Amount} {SourceAsset} from wallet {WalletId} to {Network}/{Asset}",
                request.Amount, request.SourceAsset, request.WalletId,
                request.Destination.Network, request.Destination.Asset);

            await postSettlementHandlers.SafeHandleEventAsync(
                new PostSettlementActionEvent(request, null, ActionState.Failed, ex.Message),
                cancellationToken: cancellationToken);

            return null;
        }
    }

    private async Task<SettlementContext> BuildContext(string walletId, CancellationToken cancellationToken)
    {
        var chainTime = await blockchain.GetChainTime(cancellationToken);
        var coins = await spendingService.GetAvailableCoins(walletId, cancellationToken);
        var locked = (await intentStorage.GetLockedVtxoOutpoints(walletId, cancellationToken)).ToHashSet();

        var spendable = coins
            .Where(coin => !coin.Unrolled && !coin.IsRecoverable(chainTime) && !locked.Contains(coin.Outpoint))
            .ToArray();

        // A coin carrying an Arkade-issued asset holds a dust-sized satoshi amount purely as the
        // asset's carrier, and that amount is not free to move: spending the coin for BTC would
        // take the asset with it. Counting it would let a BTC threshold fire on value the wallet
        // cannot actually settle, so the two denominations are split here and stay split all the
        // way through the policies and the per-asset remainders in ProcessWallet.
        var assetCoins = spendable.Where(coin => coin.Assets is { Count: > 0 }).ToArray();
        var btcCoins = spendable.Where(coin => coin.Assets is null or { Count: 0 }).ToArray();

        var assetBalances = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in assetCoins.SelectMany(coin => coin.Assets!))
            assetBalances[asset.AssetId] = assetBalances.GetValueOrDefault(asset.AssetId) + asset.Amount;

        return new SettlementContext(
            walletId,
            btcCoins,
            btcCoins.Sum(coin => coin.Amount.Satoshi),
            chainTime,
            assetCoins,
            assetBalances);
    }

    private async void OnVtxoChanged(object? sender, ArkVtxo vtxo)
    {
        try
        {
            var contracts = await contractStorage.GetContracts(scripts: [vtxo.Script]);
            foreach (var walletId in contracts.Select(contract => contract.WalletIdentifier).Distinct())
                QueueWallet(walletId);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to queue settlement evaluation for VTXO {Outpoint}", vtxo.OutPoint);
        }
    }

    private void OnIntentChanged(object? sender, ArkIntent intent)
    {
        // Only terminal transitions change what is spendable; mid-batch churn does not.
        if (intent.State is ArkIntentState.BatchSucceeded or ArkIntentState.BatchFailed or ArkIntentState.Cancelled)
            QueueWallet(intent.WalletId);
    }

    private void OnTriggerSourceActivity(object? sender, string walletId) => QueueWallet(walletId);
}
