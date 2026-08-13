using Microsoft.Extensions.DependencyInjection;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Services;

namespace NArk.ArkadeIntents.Hosting;

public static class ArkadeIntentsCollectionExtensions
{
    /// <summary>
    /// Registers the Arkade non-interactive swap services: solver discovery, the intent manager, the
    /// Arkade → Lightning maker client, and
    /// the covenant-VTXO monitor (a hosted service that transitions swap status via
    /// <see cref="IArkadeIntentStorage"/>). The <see cref="IArkadeIntentStorage"/> itself is provided
    /// by the storage layer (e.g. the EF Core registration), which also exposes it as an
    /// <see cref="NArk.Abstractions.Scripts.IActiveScriptsProvider"/> so its pending-swap scripts are
    /// watched by the shared VtxoSynchronizationService.
    /// </summary>
    public static IServiceCollection AddArkadeIntentsServices(this IServiceCollection services)
    {
        services.AddHttpClient<SolverDiscoveryService>();
        services.AddSingleton<ArkadeIntentManager>();
        services.AddSingleton<LightningIntentsClient>();
        services.AddSingleton<ArkadeIntentsService>();
        services.AddHostedService<ArkadeSwapIntentMonitoringService>();
        // Registered beside the monitor on purpose. The monitor only observes; without something
        // acting on what it sees, a funded receive sits at Claimable until its window closes and
        // the payment silently does not arrive. Opt out through ArkadeIntentAdvanceOptions if the
        // host means to drive claims itself.
        services.AddHostedService<ArkadeIntentAdvanceService>();
        return services;
    }
}
