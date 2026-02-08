using Microsoft.Extensions.Hosting;
using NArk.Swaps.Services;

namespace NArk.Swaps.Hosting;

public class SwapHostedLifecycle(SwapsManagementService swapsManagementService) : IHostedLifecycleService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await swapsManagementService.StartAsync(cancellationToken);
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
