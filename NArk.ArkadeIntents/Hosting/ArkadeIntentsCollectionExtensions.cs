using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NArk.ArkadeIntents.Onchain;
using Microsoft.Extensions.Logging;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Services;
using NArk.ArkadeIntents;

using NArk.ArkadeIntents.Assets;
using NArk.ArkadeIntents.Evm;
using NArk.Abstractions.Blockchain;
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
    /// <remarks>
    /// The on-chain and EVM corridors are wired only when the seam each is built on — an
    /// <see cref="IBitcoinBlockchain"/> and an <see cref="IEvmSwapRpc"/> respectively — is already
    /// in the container. Neither has a default worth inventing, so a host that registered neither
    /// gets those corridors absent rather than a container that fails to validate, and a caller
    /// that reaches for one anyway gets an error naming what is missing.
    /// </remarks>
    /// <param name="services">The container.</param>
    /// <param name="options">
    /// All supplied corridor settings are copied, including payer limits and L1 confirmation policy.
    /// With <c>null</c>, existing limits remain unchanged and the default emulator key is selected.
    /// </param>
    public static IServiceCollection AddArkadeIntentsServices(
        this IServiceCollection services, ArkadeIntentsOptions? options = null)
    {
        services.Configure<ArkadeIntentsOptions>(configured =>
        {
            configured.EmulatorPubkeyOverride = options?.EmulatorPubkeyOverride;
            if (options is not null)
            {
                configured.MaxPayAmountSats = options.MaxPayAmountSats;
                configured.OnchainClaimConfirmations = options.OnchainClaimConfirmations;
            }
        });
        // Singleton, not AddHttpClient<T>: that registers the client TRANSIENT, and the service
        // caches each registry index in an instance field. A fresh instance per injection means the
        // TTL never hits and every discovery call re-fetches every registry.
        services.AddHttpClient(nameof(SolverDiscoveryService));
        services.AddSingleton(sp => new SolverDiscoveryService(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(SolverDiscoveryService)),
            sp.GetService<ILogger<SolverDiscoveryService>>()));
        services.AddSingleton<AssetIntentsManager>();
        services.AddSingleton<LightningIntentsClient>();
        // Each of these is registered only when the seam it is built on is already in the
        // container. Neither seam has a default worth inventing — IEvmSwapRpc needs the address of
        // an EVM node, IBitcoinBlockchain an L1 source — so a host that configured neither means it
        // wants neither corridor.
        //
        // TryAdd alone does not express that: it only declines to overwrite an existing
        // registration, and still leaves a descriptor whose dependency nothing satisfies. A
        // container that validates its descriptors — BTCPayServer builds with ValidateOnBuild —
        // then fails at startup over a corridor the host never asked for, naming a type it has
        // never heard of. Absent the seam the corridor should simply be missing, which is what
        // ArkadeIntentsService already reads OnchainIntentsClient as (optional, then RequireOnchain
        // at the point of use).
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IEvmSwapRpc)))
            services.TryAddSingleton<EvmIntentsClient>();
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IBitcoinBlockchain)))
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
