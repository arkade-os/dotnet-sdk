using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NArk.ArkadeIntents.Onchain;
using Microsoft.Extensions.Logging;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Services;
using NArk.ArkadeIntents;

using NArk.ArkadeIntents.Assets;
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
    /// <param name="services">The container.</param>
    /// <param name="options">
    /// Shared corridor settings, or <c>null</c> for the defaults. Registered here so both corridors
    /// read the same ones.
    /// </param>
    public static IServiceCollection AddArkadeIntentsServices(
        this IServiceCollection services, ArkadeIntentsOptions? options = null)
    {
        services.Configure<ArkadeIntentsOptions>(configured =>
            configured.EmulatorPubkeyOverride = options?.EmulatorPubkeyOverride);
        // Singleton, not AddHttpClient<T>: that registers the client TRANSIENT, and the service
        // caches each registry index in an instance field. A fresh instance per injection means the
        // TTL never hits and every discovery call re-fetches every registry.
        services.AddHttpClient(nameof(SolverDiscoveryService));
        services.AddSingleton(sp => new SolverDiscoveryService(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(SolverDiscoveryService)),
            sp.GetService<ILogger<SolverDiscoveryService>>()));
        services.AddSingleton<AssetIntentsManager>();
        services.AddSingleton<LightningIntentsClient>();
        // TryAdd, not Add: the off-board corridor needs IBitcoinBlockchain, and a deployment with no
        // L1 access should get an ArkadeIntentsService without it rather than a resolution failure.
        services.TryAddSingleton<OnchainIntentsClient>();
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
