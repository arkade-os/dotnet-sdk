using Microsoft.Extensions.DependencyInjection;
using NArk.Core.Sweeper;
using NArk.Core.Transformers;
using NArk.Swaps.Boltz;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models;
using NArk.Swaps.Policies;
using NArk.Swaps.Services;
using NArk.Swaps.Transformers;

namespace NArk.Hosting;

/// <summary>
/// Extension methods for registering NArk swap services with IServiceCollection.
/// </summary>
public static class SwapServiceCollectionExtensions
{
    /// <summary>
    /// Registers NArk swap services (Boltz integration).
    /// Also auto-configures BoltzClientOptions from ArkNetworkConfig if registered.
    /// </summary>
    public static IServiceCollection AddArkSwapServices(this IServiceCollection services)
    {
        services.AddSingleton<SwapsManagementService>();
        services.AddSingleton<ISweepPolicy, SwapSweepPolicy>();
        services.AddSingleton<IContractTransformer, VHTLCContractTransformer>();
        
        services.AddSingleton<CachedBoltzClient>();
        services.AddSingleton<BoltzLimitsValidator>();
        services.AddHostedService<NArk.Swaps.Hosting.SwapHostedLifecycle>();

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
