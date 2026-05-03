using Microsoft.Extensions.DependencyInjection;
using NArk.Abstractions.Recovery;
using NArk.Core.Sweeper;
using NArk.Core.Transformers;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models;
using NArk.Swaps.Policies;
using NArk.Swaps.Recovery;
using NArk.Swaps.Services;
using NArk.Swaps.Transformers;

namespace NArk.Hosting;

/// <summary>
/// Extension methods for registering NArk swap services with IServiceCollection.
/// </summary>
public static class SwapServiceCollectionExtensions
{
    /// <summary>
    /// Registers core swap services (provider-agnostic) and the Boltz provider.
    /// This is the backward-compatible entry point that registers everything needed.
    /// </summary>
    public static IServiceCollection AddArkSwapServices(this IServiceCollection services)
    {
        // Core services (provider-agnostic)
        services.AddSingleton<SwapsManagementService>();
        services.AddSingleton<ISweepPolicy, SwapSweepPolicy>();
        services.AddSingleton<IContractTransformer, VHTLCContractTransformer>();
        services.AddHostedService<NArk.Swaps.Hosting.SwapHostedLifecycle>();

        // Boltz provider
        services.AddBoltzProvider();

        return services;
    }

    /// <summary>
    /// Registers the Boltz swap provider and its dependencies.
    /// Can be called separately if using manual provider registration.
    /// </summary>
    public static IServiceCollection AddBoltzProvider(this IServiceCollection services, Action<BoltzClientOptions>? configure = null)
    {
        if (configure != null)
            services.Configure(configure);

        services.AddSingleton<IContractDiscoveryProvider, BoltzSwapDiscoveryProvider>();
        services.AddSingleton<CachedBoltzClient>();
        services.AddSingleton<BoltzLimitsValidator>();
        services.AddSingleton<BoltzSwapProvider>();
        services.AddSingleton<ISwapProvider>(sp => sp.GetRequiredService<BoltzSwapProvider>());

        // Auto-configure BoltzClientOptions from ArkNetworkConfig if available
        services.AddOptions<BoltzClientOptions>()
            .Configure<ArkNetworkConfig>((boltz, config) =>
            {
                if (!string.IsNullOrWhiteSpace(config.BoltzUri))
                {
                    boltz.BoltzUrl ??= config.BoltzUri;
                    boltz.WebsocketUrl ??= config.BoltzUri;
                }
            });

        return services;
    }

}
