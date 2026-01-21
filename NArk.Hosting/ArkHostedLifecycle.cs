using Microsoft.Extensions.Hosting;
using NArk.Core.Services;

namespace NArk.Hosting;

public class ArkHostedLifecycle(
    VtxoSynchronizationService vtxoSynchronizationService,
    IntentGenerationService intentGenerationService,
    IntentSynchronizationService intentSynchronizationService,
    BatchManagementService batchManagementService,
    SweeperService sweeperService) : IHostedLifecycleService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await sweeperService.StartAsync(cancellationToken);
        await batchManagementService.StartAsync(cancellationToken);
        await intentSynchronizationService.StartAsync(cancellationToken);
        await intentGenerationService.StartAsync(cancellationToken);
        await vtxoSynchronizationService.StartAsync(cancellationToken);
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