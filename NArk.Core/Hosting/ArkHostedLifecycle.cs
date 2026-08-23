using Microsoft.Extensions.Hosting;
using NArk.Core.Services;

namespace NArk.Hosting;

public class ArkHostedLifecycle(
    VtxoSynchronizationService vtxoSynchronizationService,
    IntentGenerationService intentGenerationService,
    IntentSynchronizationService intentSynchronizationService,
    BatchManagementService batchManagementService,
    SweeperService sweeperService,
    ContractReconciliationService contractReconciliationService,
    PendingArkTransactionRecoveryService pendingArkTransactionRecoveryService) : IHostedLifecycleService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await sweeperService.StartAsync(cancellationToken);
        await contractReconciliationService.StartAsync(cancellationToken);
        await batchManagementService.StartAsync(cancellationToken);
        await intentSynchronizationService.StartAsync(cancellationToken);
        await intentGenerationService.StartAsync(cancellationToken);
        await vtxoSynchronizationService.StartAsync(cancellationToken);

        // Run AFTER vtxo sync so the recovery loop has fresh local VTXO state to
        // resolve checkpoint inputs against. Best-effort: failures inside here
        // are absorbed and logged so they never block the host from starting.
        await pendingArkTransactionRecoveryService.RecoverAllWalletsAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
