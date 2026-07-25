using Microsoft.Extensions.DependencyInjection;
using NArk.Swaps.Abstractions;

namespace NArk.Swaps.Evm.Hosting;

/// <summary>
/// Extension methods for registering <see cref="EvmChainSwapProvider"/> with
/// <see cref="IServiceCollection"/>, mirroring the shape of
/// <c>NArk.Hosting.SwapServiceCollectionExtensions.AddBoltzProvider</c>. Not folded into
/// <c>AddArkSwapServices()</c> — this provider is opt-in (needs an EVM RPC URL and signing
/// key configured), unlike Boltz which every Ark swap consumer needs.
/// </summary>
public static class EvmSwapServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="EvmChainSwapProvider"/> and its dependencies. Requires
    /// <c>AddArkSwapServices()</c> (or at least <c>AddBoltzProvider()</c>) to already be
    /// registered, since this provider reuses the existing <c>BoltzClient</c> and
    /// <c>BoltzClientOptions</c> to talk to the same Boltz backend.
    /// </summary>
    public static IServiceCollection AddEvmChainSwapProvider(
        this IServiceCollection services, Action<EvmSwapOptions> configure)
    {
        services.Configure(configure);

        // No separate HttpClient here: EvmChainSwapProvider reuses BoltzClient.HttpClient,
        // which already talks to the same Boltz backend — see BoltzClient.HttpClient's doc.
        services.AddSingleton<EvmChainSwapProvider>();
        services.AddSingleton<ISwapProvider>(sp => sp.GetRequiredService<EvmChainSwapProvider>());

        return services;
    }
}
