using Microsoft.Extensions.Hosting;
using NArk.Core.Services;
using NArk.Swaps.Services;

namespace NArk.Hosting;

public class ArkHostedLifecycle(
    VtxoSynchronizationService vtxoSynchronizationService,
    IntentGenerationService intentGenerationService,
    IntentSynchronizationService intentSynchronizationService,
    BatchManagementService batchManagementService,
    SweeperService sweeperService,
    SwapsManagementService? swapsManagementService = null) : IHostedLifecycleService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await sweeperService.StartAsync(cancellationToken);
        await batchManagementService.StartAsync(cancellationToken);
        await intentSynchronizationService.StartAsync(cancellationToken);
        await intentGenerationService.StartAsync(cancellationToken);
        await vtxoSynchronizationService.StartAsync(cancellationToken);
        if (swapsManagementService is not null)
        {
            await swapsManagementService.StartAsync(cancellationToken);
        }
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