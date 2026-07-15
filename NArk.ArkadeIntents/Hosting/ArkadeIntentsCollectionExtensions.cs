using Microsoft.Extensions.DependencyInjection;
using NArk.Arkade.NonInteractiveSwaps;

namespace NArk.ArkadeIntents;

public static class ArkadeIntentsCollectionExtensions
{
    /// <summary>
    /// Registers the Arkade non-interactive swap services: solver discovery, the intent manager and
    /// the covenant-VTXO monitor (a hosted service that transitions swap status via
    /// <see cref="IArkadeIntentStorage"/>). The <see cref="IArkadeIntentStorage"/> itself is provided
    /// by the storage layer (e.g. the EF Core registration), which also exposes it as an
    /// <see cref="NArk.Abstractions.Scripts.IActiveScriptsProvider"/> so its pending-swap scripts are
    /// watched by the shared VtxoSynchronizationService.
    /// </summary>
    public static IServiceCollection AddArkadeIntentsServices(this IServiceCollection services)
    {
        services.AddHttpClient<SolverDiscoveryService>();
        services.AddHostedService<SwapIntentMonitoringService>();
        return services;
    }
}
