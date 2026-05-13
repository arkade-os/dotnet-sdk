using Microsoft.Extensions.DependencyInjection;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models;
using static NArk.Hosting.AppExtensions;

namespace NArk.Hosting;

/// <summary>
/// Swap-related extension methods for ArkApplicationBuilder.
/// </summary>
public static class SwapApplicationBuilderExtensions
{
    public static ArkApplicationBuilder WithSwapStorage<TStorage>(this ArkApplicationBuilder builder)
        where TStorage : class, ISwapStorage
    {
        builder.ConfigureServices((_, services) => { services.AddSingleton<ISwapStorage, TStorage>(); });
        return builder;
    }

    public static ArkApplicationBuilder OnCustomBoltz(this ArkApplicationBuilder builder, string boltzUrl, string? websocketUrl)
    {
        builder.ConfigureServices((_, services) =>
        {
            services.Configure<BoltzClientOptions>(b =>
            {
                b.BoltzUrl = boltzUrl;
                b.WebsocketUrl = websocketUrl ?? boltzUrl;
            });
        });
        return builder.EnableSwaps();
    }

    public static ArkApplicationBuilder EnableSwaps(this ArkApplicationBuilder builder, Action<BoltzClientOptions>? boltzOptionsConfigure = null)
    {
        builder.ConfigureServices((_, services) =>
        {
            if (boltzOptionsConfigure != null)
            {
                services.Configure(boltzOptionsConfigure);
            }

            // BoltzClient registration moved into AddBoltzProvider (called by AddArkSwapServices)
            // so direct-DI consumers get a self-contained call. The line kept here as a no-op
            // safety net for any external caller relying on the prior call ordering.
            services.AddHttpClient<BoltzClient>();
            services.AddArkSwapServices();
        });
        return builder;
    }
}
