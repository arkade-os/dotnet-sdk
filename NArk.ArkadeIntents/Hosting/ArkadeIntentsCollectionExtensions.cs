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
using NArk.ArkadeIntents.Covclaim;
using Microsoft.Extensions.Options;
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

    /// <summary>
    /// Points the receive corridors at a covclaimd instance, so every receive is revealed to it and
    /// the daemon races this wallet for the claim.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="configure">Where the daemon is, and how patient to be with it.</param>
    /// <returns>The same container, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Entirely optional, and additive: without this call a receive works exactly as before, claimed
    /// by <see cref="ArkadeIntentAdvanceService"/> the moment the monitor sees the lockup funded.
    /// What it buys is a second claimant for the window in which this wallet is not running — the
    /// daemon spends the same covenant leaf, pinned to the same payout script, so the two racing
    /// cannot disagree about where the money goes.
    /// </para>
    /// <para>
    /// Call it <b>after</b> <see cref="AddArkadeIntentsServices"/>: the corridor clients resolve the
    /// daemon as an optional dependency, and a container that registers it later still wires it,
    /// but the renewal loop reads options that this call configures.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddCovclaimd(
        this IServiceCollection services,
        Action<CovclaimdOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);

        // A named client taken from the factory at resolve time, not a typed one: the client is a
        // singleton because it caches the daemon's keys, and a typed client would pin one handler
        // for the life of the process and never see a DNS change. Connection lifetime is managed on
        // the handler instead.
        services.AddHttpClient(CovclaimdOptions.HttpClientName)
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                // A redirect would move the key fetch off the address the caller vetted, which is
                // the one thing this transport cannot afford to let a remote decide.
                AllowAutoRedirect = false,
            });

        services.TryAddSingleton<ICovclaimdClient>(sp => new CovclaimdClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(CovclaimdOptions.HttpClientName),
            sp.GetRequiredService<IOptions<CovclaimdOptions>>(),
            sp.GetService<IAesGcmCipher>(),
            sp.GetService<ILogger<CovclaimdClient>>()));

        // Registrations live in the daemon's memory, so one made at swap time is gone after a
        // restart or a TTL. Registering the loop beside the client keeps "revealed once" from
        // quietly meaning "revealed until covclaimd next restarts".
        services.AddHostedService<CovclaimdRenewalService>();
        return services;
    }
}
