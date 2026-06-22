using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.VTXOs;
using NArk.Core.Enums;
using NArk.Core.Events;
using NArk.Core.Models.Options;
using NArk.Core.Sweeper;
using NArk.Core.Extensions;

namespace NArk.Core.Services;

public class SweeperService(
    IEnumerable<ISweepPolicy> policies,
    IVtxoStorage vtxoStorage,
    ICoinService coinService,
    IContractStorage contractStorage,
    ISpendingService spendingService,
    IIntentStorage intentStorage,
    IOptions<SweeperServiceOptions> options,
    IBitcoinBlockchain chainTimeProvider,
    IEnumerable<IEventHandler<PostSweepActionEvent>> postSweepHandlers,
    ILogger<SweeperService>? logger = null) : IAsyncDisposable
{

    private record SweepJobTrigger;
    private record SweepTimerTrigger : SweepJobTrigger;
    private record SweepVtxoTrigger(IReadOnlyCollection<ArkVtxo> Vtxos) : SweepJobTrigger;
    private record SweepContractTrigger(IReadOnlyCollection<ArkContractEntity> Contracts) : SweepJobTrigger;

    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Channel<SweepJobTrigger> _sweepJobTrigger = Channel.CreateUnbounded<SweepJobTrigger>();

    private Task? _sweeperTask;
    private Timer? _timer;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger?.LogInformation("Starting sweeper service");
        var multiToken = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token, cancellationToken);
        _sweeperTask = DoSweepingLoop(multiToken.Token);
        vtxoStorage.VtxosChanged += OnVtxosChanged;
        contractStorage.ContractsChanged += OnContractsChanged;
        if (options.Value.ForceRefreshInterval != TimeSpan.Zero)
            _timer = new Timer(_ => _sweepJobTrigger.Writer.TryWrite(new SweepTimerTrigger()), null, TimeSpan.Zero,
                options.Value.ForceRefreshInterval);
        logger?.LogDebug("Sweeper service started with refresh interval {Interval}", options.Value.ForceRefreshInterval);
    }

    private async Task DoSweepingLoop(CancellationToken loopShutdownToken)
    {
        await foreach (var reason in _sweepJobTrigger.Reader.ReadAllAsync(loopShutdownToken))
        {
            // TEMP latency probe — make queue backlog observable.
            var swTrigger = System.Diagnostics.Stopwatch.StartNew();
            logger?.LogTrace("[sweep-probe] sweep trigger dequeued: {Type}", reason.GetType().Name);
            try
            {

                await (reason switch
                {
                    SweepVtxoTrigger vtxoTrigger => TrySweepVtxos(vtxoTrigger.Vtxos, loopShutdownToken),
                    SweepContractTrigger contractTrigger => TrySweepContracts(contractTrigger.Contracts, loopShutdownToken),
                    SweepTimerTrigger _ => TrySweepContracts(null, loopShutdownToken),
                    _ => throw new ArgumentOutOfRangeException()
                });

            }
            catch (Exception e)
            {
                logger?.LogInformation(0, e, "Error during sweeping loop execution for trigger {TriggerType}", reason.GetType().Name);
            }
            logger?.LogTrace("[sweep-probe] sweep trigger {Type} done in {Ms}ms",
                reason.GetType().Name, swTrigger.ElapsedMilliseconds);
        }
    }

    private async Task TrySweepContracts(IReadOnlyCollection<ArkContractEntity>? contracts,
        CancellationToken cancellationToken)
    {
        var timeHeight = await chainTimeProvider.GetChainTime(cancellationToken);
        Dictionary<string, ArkVtxo[]> matchingVtxos;

        if (contracts is null)
        {
            var unspentVtxos = await vtxoStorage.GetVtxos(includeSpent: false, cancellationToken: cancellationToken);
            var spendableVtxos = unspentVtxos.Where(v => v.CanSpendOffchain(timeHeight)).ToArray();
            contracts =
                await contractStorage.GetContracts(
                    scripts: spendableVtxos.Select(v => v.Script).Distinct().ToArray(),
                    cancellationToken: cancellationToken);

            matchingVtxos =
                spendableVtxos
                .GroupBy(vtxo => vtxo.Script).ToDictionary(vtxos => vtxos.Key, vtxos => vtxos.ToArray());

        }
        else
        {

            var contractScripts = contracts.Select(c => c.Script).ToHashSet();
            var unspentVtxos = await vtxoStorage.GetVtxos(includeSpent: false, scripts: contractScripts, cancellationToken: cancellationToken);
            var spendableVtxos = unspentVtxos.Where(v => v.CanSpendOffchain(timeHeight)).ToArray();
            matchingVtxos =
                spendableVtxos
                    .GroupBy(vtxo => vtxo.Script).ToDictionary(vtxos => vtxos.Key, vtxos => vtxos.ToArray());
        }

        Dictionary<ArkContractEntity, ArkVtxo[]> contractVtxos = new Dictionary<ArkContractEntity, ArkVtxo[]>();
        foreach (var contract in contracts)
        {
            if (!matchingVtxos.TryGetValue(contract.Script, out var vtxos))
                continue;

            contractVtxos.Add(contract, vtxos.ToArray());

        }

        var transformedCoins = await GetCoinsAsync(contractVtxos);
        await ExecutePoliciesAsync(transformedCoins);
    }

    private async Task<List<ArkCoin>> GetCoinsAsync(Dictionary<ArkContractEntity, ArkVtxo[]> coins)
    {
        var result = new List<ArkCoin>();
        foreach (var contractCoins in coins)
            foreach (var vtxo in contractCoins.Value)
            {
                // TEMP latency probe per VTXO.
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    result.Add(await coinService.GetCoin(contractCoins.Key, vtxo));
                    logger?.LogTrace(
                        "[sweep-probe]     GetCoin {Type} {Outpoint}: {Ms}ms",
                        contractCoins.Key.Type, $"{vtxo.TransactionId[..8]}:{vtxo.TransactionOutputIndex}", sw.ElapsedMilliseconds);
                }
                catch (AdditionalInformationRequiredException)
                {
                    // Skip unsignable contracts (e.g. UnknownArkContract for sweep destinations)
                    logger?.LogTrace(
                        "[sweep-probe]     GetCoin {Type} {Outpoint}: skipped (AdditionalInformationRequired) after {Ms}ms",
                        contractCoins.Key.Type, $"{vtxo.TransactionId[..8]}:{vtxo.TransactionOutputIndex}", sw.ElapsedMilliseconds);
                }
            }
        return result;
    }

    private async Task TrySweepVtxos(IReadOnlyCollection<ArkVtxo> vtxos, CancellationToken cancellationToken)
    {
        // TEMP latency probe — pinpointing the 11.5s gap between HTLC arrival
        // and "VHTLC claim:" log in reverse-submarine-swap settlement. Remove
        // (or demote to Debug) once the bottleneck is identified.
        var swTotal = System.Diagnostics.Stopwatch.StartNew();
        logger?.LogTrace("[sweep-probe] TrySweepVtxos enter: {VtxoCount} VTXO(s) [{Outpoints}]",
            vtxos.Count,
            string.Join(", ", vtxos.Select(v => $"{v.TransactionId[..8]}:{v.TransactionOutputIndex}")));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var timeHeight = await chainTimeProvider.GetChainTime(cancellationToken);
        logger?.LogTrace("[sweep-probe]   GetChainTime: {Ms}ms", sw.ElapsedMilliseconds);

        var unspentVtxos = vtxos.Where(v => v.CanSpendOffchain(timeHeight)).ToArray();
        if (unspentVtxos.Length == 0)
        {
            logger?.LogTrace("[sweep-probe] TrySweepVtxos exit (no spendable): total={Ms}ms", swTotal.ElapsedMilliseconds);
            return;
        }
        var scriptToVtxos = unspentVtxos.GroupBy(vtxo => vtxo.Script)
            .ToDictionary(grouping => grouping.Key, grouping => grouping.ToArray());

        sw.Restart();
        var contracts =
            await contractStorage.GetContracts(scripts: scriptToVtxos.Keys.ToArray(), cancellationToken: cancellationToken);
        logger?.LogTrace("[sweep-probe]   GetContracts ({ScriptCount} scripts): {Ms}ms, {ContractCount} matched",
            scriptToVtxos.Count, sw.ElapsedMilliseconds, contracts.Count);

        Dictionary<ArkContractEntity, ArkVtxo[]> contractVtxos = new Dictionary<ArkContractEntity, ArkVtxo[]>();
        foreach (var contract in contracts)
        {
            if (!scriptToVtxos.TryGetValue(contract.Script, out var x))
                continue;

            contractVtxos.Add(contract, x.ToArray());
        }

        sw.Restart();
        var transformedCoins = await GetCoinsAsync(contractVtxos);
        logger?.LogTrace("[sweep-probe]   GetCoinsAsync ({InputContracts} contracts → {CoinCount} coins): {Ms}ms",
            contractVtxos.Count, transformedCoins.Count, sw.ElapsedMilliseconds);

        sw.Restart();
        await ExecutePoliciesAsync(transformedCoins);
        logger?.LogTrace("[sweep-probe]   ExecutePoliciesAsync: {Ms}ms", sw.ElapsedMilliseconds);

        logger?.LogTrace("[sweep-probe] TrySweepVtxos exit: total={Ms}ms", swTotal.ElapsedMilliseconds);
    }

    private async Task ExecutePoliciesAsync(IReadOnlyCollection<ArkCoin> coins)
    {
        if (coins.Count == 0)
            return;

        HashSet<ArkCoin> coinsToSweep = [];

        // TEMP latency probe.
        foreach (var policy in policies)
        {
            var swPolicy = System.Diagnostics.Stopwatch.StartNew();
            var beforeCount = coinsToSweep.Count;
            await foreach (var coin in policy.SweepAsync(coins))
            {
                coinsToSweep.Add(coin);
            }
            logger?.LogTrace(
                "[sweep-probe]     policy {Policy}: {Ms}ms (+{Added} coins to sweep)",
                policy.GetType().Name, swPolicy.ElapsedMilliseconds, coinsToSweep.Count - beforeCount);
        }

        var swSweep = System.Diagnostics.Stopwatch.StartNew();
        await Sweep(coinsToSweep);
        logger?.LogTrace(
            "[sweep-probe]     Sweep ({Count} coins): {Ms}ms", coinsToSweep.Count, swSweep.ElapsedMilliseconds);
    }

    private async Task Sweep(HashSet<ArkCoin> coinsToSweep)
    {
        logger?.LogDebug("Starting sweep for {OutpointCount} coins", coinsToSweep.Count);

        // Skip VTXOs registered with arkd via pending batch intents
        var batchIntents = await intentStorage.GetIntents(
            walletIds: coinsToSweep.Select(c => c.WalletIdentifier).Distinct().ToArray(),
            states: [ArkIntentState.WaitingForBatch]);
        var lockedOutpoints = batchIntents
            .SelectMany(i => i.IntentVtxos)
            .ToHashSet();

        foreach (var coin in coinsToSweep)
        {
            // Tag every log line emitted during this coin's sweep with the
            // owning wallet so per-wallet diagnostic-log capture can route
            // them. The set may span multiple wallets; the scope is per
            // iteration, not per call.
            using var _walletScope = logger?.BeginScope(("WalletId", coin.WalletIdentifier));

            if (lockedOutpoints.Contains(coin.Outpoint))
            {
                logger?.LogDebug("Sweep skipped for outpoint {Outpoint}: locked by pending intent", coin.Outpoint);
                continue;
            }
            try
            {
                var txId = await spendingService.Spend(coin.WalletIdentifier, [coin], [],
                    CancellationToken.None);
                logger?.LogInformation("Sweep successful for outpoint {Outpoint}, txId: {TxId}", coin.Outpoint, txId);
                await postSweepHandlers.SafeHandleEventAsync(new PostSweepActionEvent(coin, txId,
                    ActionState.Successful, null));
            }
            catch (AlreadyLockedVtxoException ex)
            {
                logger?.LogWarning(0, ex, "Sweep skipped for outpoint {Outpoint}: vtxo is already locked", coin.Outpoint);
                await postSweepHandlers.SafeHandleEventAsync(new PostSweepActionEvent(coin, null,
                    ActionState.Failed, "Vtxo is already locked by another process."));
            }
            catch (Exception ex)
            {
                logger?.LogDebug(0, ex, "Sweep failed for outpoint {Outpoint}", coin.Outpoint);
                await postSweepHandlers.SafeHandleEventAsync(new PostSweepActionEvent(coin, null,
                    ActionState.Failed, ex.Message));
            }
        }
    }

    private void OnContractsChanged(object? sender, ArkContractEntity e) =>
        _sweepJobTrigger.Writer.TryWrite(new SweepContractTrigger([e]));

    private void OnVtxosChanged(object? sender, ArkVtxo e) =>
        _sweepJobTrigger.Writer.TryWrite(new SweepVtxoTrigger([e]));

    public async ValueTask DisposeAsync()
    {
        logger?.LogDebug("Disposing sweeper service");
        vtxoStorage.VtxosChanged -= OnVtxosChanged;
        contractStorage.ContractsChanged -= OnContractsChanged;

        try
        {
            if (_timer is not null)
                await _timer.DisposeAsync();
        }
        catch (Exception ex)
        {
            logger?.LogDebug(0, ex, "Error disposing timer during sweeper service shutdown");
        }

        try
        {
            await _shutdownCts.CancelAsync();
        }
        catch (Exception ex)
        {
            logger?.LogDebug(0, ex, "Error cancelling shutdown token during sweeper service shutdown");
        }

        try
        {
            if (_sweeperTask is not null)
                await _sweeperTask;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(0, ex, "Sweeper task completed with error during shutdown");
        }

        logger?.LogInformation("Sweeper service disposed");
    }
}