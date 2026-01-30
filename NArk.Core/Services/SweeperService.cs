using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
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
    IOptions<SweeperServiceOptions> options,
    IChainTimeProvider chainTimeProvider,
    IEnumerable<IEventHandler<PostSweepActionEvent>> postSweepHandlers,
    ILogger<SweeperService>? logger = null) : IAsyncDisposable
{
    public SweeperService(
        IEnumerable<ISweepPolicy> policies,
        IVtxoStorage vtxoStorage,
        ICoinService coinService,
        IContractStorage contractStorage,
        ISpendingService spendingService,
        IOptions<SweeperServiceOptions> options,
        IChainTimeProvider chainTimeProvider,
        ILogger<SweeperService> logger)
            : this(policies, vtxoStorage, coinService, contractStorage, spendingService, options, chainTimeProvider, [], logger)
    {
    }

    public SweeperService(
        IEnumerable<ISweepPolicy> policies,
        IVtxoStorage vtxoStorage,
        ICoinService coinService,
        IContractStorage contractStorage,
        ISpendingService spendingService,
        IOptions<SweeperServiceOptions> options,
        IChainTimeProvider chainTimeProvider)
        : this(policies, vtxoStorage, coinService, contractStorage, spendingService, options, chainTimeProvider, [], null)
    {
    }

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
            await (reason switch
            {
                SweepVtxoTrigger vtxoTrigger => TrySweepVtxos(vtxoTrigger.Vtxos, loopShutdownToken),
                SweepContractTrigger contractTrigger => TrySweepContracts(contractTrigger.Contracts, loopShutdownToken),
                SweepTimerTrigger _ => TrySweepContracts(null, loopShutdownToken),
                _ => throw new ArgumentOutOfRangeException()
            });
        }
    }

    private async Task TrySweepContracts(IReadOnlyCollection<ArkContractEntity>? contracts,
        CancellationToken cancellationToken)
    {
        Dictionary<string, ArkVtxo[]> matchingVtxos;

        if (contracts is null)
        {
            var unspentVtxos = await vtxoStorage.GetVtxos(includeSpent: false, cancellationToken: cancellationToken);
            contracts =
                await contractStorage.GetContracts(
                    scripts: unspentVtxos.Select(v => v.Script).Distinct().ToArray(),
                    cancellationToken: cancellationToken);
            
            matchingVtxos =
                unspentVtxos
                .GroupBy(vtxo => vtxo.Script).ToDictionary(vtxos => vtxos.Key, vtxos => vtxos.ToArray());
            
        }
        else
        {
            
            var contractScripts = contracts.Select(c => c.Script).ToHashSet();
            var unspentVtxos = await vtxoStorage.GetVtxos(includeSpent: false, scripts: contractScripts , cancellationToken: cancellationToken);
            matchingVtxos =
                unspentVtxos
                    .GroupBy(vtxo => vtxo.Script).ToDictionary(vtxos => vtxos.Key, vtxos => vtxos.ToArray());
        }

        Dictionary<ArkContractEntity, ArkVtxo[]> contractVtxos = new Dictionary<ArkContractEntity, ArkVtxo[]>();
        foreach (var contract in contracts)
        {
            if (!matchingVtxos.TryGetValue(contract.Script, out var vtxos))
                continue;

            contractVtxos.Add(contract, vtxos.ToArray());

        }

        var transformedCoins = await GetCoins(contractVtxos).ToListAsync(cancellationToken);
        await ExecutePoliciesAsync(transformedCoins);
    }

    private async IAsyncEnumerable<ArkCoin> GetCoins(Dictionary<ArkContractEntity, ArkVtxo[]> coins)
    {
        foreach (var contractCoins in coins)
            foreach (var vtxo in contractCoins.Value)
                yield return await coinService.GetCoin(contractCoins.Key, vtxo);
    }

    private async Task TrySweepVtxos(IReadOnlyCollection<ArkVtxo> vtxos, CancellationToken cancellationToken)
    {
        var unspentVtxos = vtxos.Where(v => !v.IsSpent()).ToArray();
        var scriptToVtxos = unspentVtxos.GroupBy(vtxo => vtxo.Script)
            .ToDictionary(grouping => grouping.Key, grouping => grouping.ToArray());

        var contracts =
            await contractStorage.GetContracts(scripts: scriptToVtxos.Keys.ToArray(), cancellationToken: cancellationToken);

        Dictionary<ArkContractEntity, ArkVtxo[]> contractVtxos = new Dictionary<ArkContractEntity, ArkVtxo[]>();
        foreach (var contract in contracts)
        {
            if (!scriptToVtxos.TryGetValue(contract.Script, out var x))
                continue;

            contractVtxos.Add(contract, x.ToArray());
        }

        var transformedCoins = await GetCoins(contractVtxos).ToListAsync(cancellationToken: cancellationToken);
        await ExecutePoliciesAsync(transformedCoins);
    }

    private async Task ExecutePoliciesAsync(IReadOnlyCollection<ArkCoin> coins)
    {
        HashSet<ArkCoin> coinsToSweep = [];

        foreach (var policy in policies)
        {
            await foreach (var coin in policy.SweepAsync(coins))
            {
                coinsToSweep.Add(coin);
            }
        }

        await Sweep(coinsToSweep);
    }

    private async Task Sweep(HashSet<ArkCoin> coinsToSweep)
    {
        var timeHeight = await chainTimeProvider.GetChainTime();
        logger?.LogDebug("Starting sweep for {OutpointCount} coins", coinsToSweep.Count);

        foreach (var coin in coinsToSweep)
        {
            if (!coin.CanSpendOffchain(timeHeight))
            {
                // Skip recoverable coins - IntentScheduler handles renewal before expiry
                logger?.LogDebug("Skipping recoverable coin {Outpoint} - handled by IntentScheduler", coin.Outpoint);
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