using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using NArk.Abstractions.Fees;
using NArk.Abstractions.Wallets;
using NArk.Abstractions.Recovery;
using NArk.Core.CoinSelector;
using NArk.Core.Events;
using NArk.Core.Fees;
using NArk.Core.Models.Options;
using NArk.Core.Recovery;
using NArk.Core.Services;
using NArk.Core.Sweeper;
using NArk.Abstractions.Services;
using NArk.Core.Transformers;
using NArk.Core.Transport;
using NArk.Core.Wallet;
using NArk.Transport.GrpcClient;
using NArk.Transport.RestClient;
using Microsoft.Extensions.Logging;

namespace NArk.Hosting;

/// <summary>
/// Network configuration for Ark services.
/// Contains URIs for Ark server, Arkade wallet, and Boltz swap service.
/// </summary>
public record ArkNetworkConfig(
    [property: JsonPropertyName("ark")]
    string ArkUri,

    [property: JsonPropertyName("arkade-wallet")]
    string? ArkadeWalletUri = null,

    [property: JsonPropertyName("boltz")]
    string? BoltzUri = null,

    [property: JsonPropertyName("explorer")]
    string? ExplorerUri = null)
{
    /// <summary>Mainnet configuration.</summary>
    public static readonly ArkNetworkConfig Mainnet = new(
        ArkUri: "https://arkade.computer",
        ArkadeWalletUri: "https://arkade.money",
        BoltzUri: "https://api.ark.boltz.exchange/",
        ExplorerUri: "https://arkade.space");

    /// <summary>Mutinynet (signet) configuration.</summary>
    public static readonly ArkNetworkConfig Mutinynet = new(
        ArkUri: "https://mutinynet.arkade.sh",
        ArkadeWalletUri: "https://mutinynet.arkade.money",
        BoltzUri: "https://api.boltz.mutinynet.arkade.sh/",
        ExplorerUri: "https://explorer.mutinynet.arkade.sh");

    /// <summary>Local regtest configuration.</summary>
    public static readonly ArkNetworkConfig Regtest = new(
        ArkUri: "http://localhost:7070",
        ArkadeWalletUri: "http://localhost:3002",
        BoltzUri: "http://localhost:9069/",
        ExplorerUri: "http://localhost:7080");

}

/// <summary>
/// Extension methods for registering NArk services with IServiceCollection.
/// Use this when you don't have access to IHostBuilder (e.g., in plugin scenarios).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all NArk core services including VTXO polling event handlers.
    /// Caller must still register: IVtxoStorage, IContractStorage, IIntentStorage, IWalletStorage,
    /// ISwapStorage, IWallet, ISafetyService, IChainTimeProvider, and IClientTransport.
    /// </summary>
    public static IServiceCollection AddArkCoreServices(this IServiceCollection services)
    {
        services.AddSingleton<ICoinService, CoinService>();
        services.AddTransient<IContractTransformer, PaymentContractTransformer>();
        services.AddTransient<IContractTransformer, NoteContractTransformer>();
        services.AddTransient<IContractTransformer, HashLockedContractTransformer>();
        services.AddTransient<IContractTransformer, BoardingContractTransformer>();
        services.AddSingleton<SpendingService>();
        services.AddSingleton<ISpendingService>(s => s.GetRequiredService<SpendingService>());
        services.AddSingleton<IContractService, ContractService>();
        services.AddSingleton<VtxoSynchronizationService>();
        services.AddSingleton<IntentGenerationService>();
        services.AddSingleton<IIntentGenerationService>(s => s.GetRequiredService<IntentGenerationService>());
        services.AddSingleton<IntentSynchronizationService>();
        services.AddSingleton<BatchManagementService>();
        services.AddSingleton<IOnchainService, OnchainService>();
        services.AddSingleton<SweeperService>();
        services.AddSingleton<IFeeEstimator, DefaultFeeEstimator>();
        services.AddSingleton<ICoinSelector, DefaultCoinSelector>();
        services.AddHostedService<ArkHostedLifecycle>();

        // Delegation services are opt-in via AddArkDelegation (they require an
        // IDelegatorProvider that only the caller knows how to configure).

        // VTXO polling - automatically poll for updates after batch success and spend transactions
        services.AddVtxoPolling();

        // HD-wallet recovery: gap-limit scan for prior contract usage on import.
        services.AddSingleton<HdWalletRecoveryService>();
        services.AddSingleton<IContractDiscoveryProvider, IndexerVtxoDiscoveryProvider>();
        // BoardingUtxoDiscoveryProvider activates only when an IBoardingUtxoProvider has
        // been registered (typically by the plugin via NBXplorer/Esplora). When absent the
        // provider is a no-op so HD recovery still works without on-chain probing.
        services.AddSingleton<IContractDiscoveryProvider>(sp =>
            sp.GetService<IBoardingUtxoProvider>() is { } utxoProvider
                ? new BoardingUtxoDiscoveryProvider(
                    utxoProvider,
                    sp.GetRequiredService<IClientTransport>(),
                    sp.GetService<ILogger<BoardingUtxoDiscoveryProvider>>())
                : NullContractDiscoveryProvider.Instance);

        return services;
    }

    /// <summary>
    /// Registers the Ark network configuration and configures transport services.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="config">The network configuration.</param>
    public static IServiceCollection AddArkNetwork(this IServiceCollection services, ArkNetworkConfig config)
    {
        // Register the config itself for injection
        services.AddSingleton(config);

        // Register the raw gRPC transport
        services.AddSingleton(_ => new GrpcClientTransport(config.ArkUri));

        // Register IClientTransport with caching wrapper as the default
        services.AddSingleton<IClientTransport>(sp =>
        {
            var inner = sp.GetRequiredService<GrpcClientTransport>();
            var logger = sp.GetService<ILogger<CachingClientTransport>>();
            return new CachingClientTransport(inner, logger);
        });

        return services;
    }

    /// <summary>
    /// Registers the Ark network using HTTP/REST + SSE transport instead of gRPC.
    /// Use this when gRPC is unavailable (e.g., browser WASM, HTTP-only environments).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="config">The network configuration.</param>
    public static IServiceCollection AddArkRestTransport(this IServiceCollection services, ArkNetworkConfig config)
    {
        // Register the config itself for injection
        services.AddSingleton(config);

        // Register the REST transport
        services.AddSingleton(_ => new RestClientTransport(config.ArkUri));

        // Register IClientTransport with caching wrapper as the default
        services.AddSingleton<IClientTransport>(sp =>
        {
            var inner = sp.GetRequiredService<RestClientTransport>();
            var logger = sp.GetService<ILogger<CachingClientTransport>>();
            return new CachingClientTransport(inner, logger);
        });

        return services;
    }

    /// <summary>
    /// Registers mainnet Ark network configuration.
    /// </summary>
    public static IServiceCollection AddArkMainnet(this IServiceCollection services)
        => services.AddArkNetwork(ArkNetworkConfig.Mainnet);

    /// <summary>
    /// Registers Mutinynet Ark network configuration.
    /// </summary>
    public static IServiceCollection AddArkMutinynet(this IServiceCollection services)
        => services.AddArkNetwork(ArkNetworkConfig.Mutinynet);

    /// <summary>
    /// Registers regtest Ark network configuration.
    /// </summary>
    public static IServiceCollection AddArkRegtest(this IServiceCollection services)
        => services.AddArkNetwork(ArkNetworkConfig.Regtest);

    /// <summary>
    /// Registers automated VTXO delegation services.
    /// Call this in addition to <see cref="AddArkCoreServices"/> and AFTER registering
    /// <see cref="IWalletProvider"/>. This will:
    /// - Register the gRPC delegator provider
    /// - Register DelegateContractTransformer (makes delegate VTXOs spendable)
    /// - Decorate IWalletProvider to produce ArkDelegateContract for HD wallets
    /// - Register DelegationMonitorService to auto-delegate new VTXOs
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="delegatorUri">The URI of the Fulmine delegator gRPC endpoint.</param>
    public static IServiceCollection AddArkDelegation(this IServiceCollection services, string delegatorUri)
    {
        services.AddSingleton<IDelegatorProvider>(_ => new GrpcDelegatorProvider(delegatorUri));
        services.AddTransient<IContractTransformer, DelegateContractTransformer>();
        services.AddTransient<IDelegationTransformer, DelegateContractDelegationTransformer>();
        services.AddSingleton<DelegationService>();

        // Decorate IWalletProvider: wrap the existing registration with DelegatingWalletProvider
        // that overrides contract derivation to produce ArkDelegateContract for HD wallets.
        // Uses the same pattern as CachingClientTransport wrapping GrpcClientTransport.
        var existingDescriptor = services.LastOrDefault(d => d.ServiceType == typeof(IWalletProvider));
        if (existingDescriptor is not null)
        {
            services.Remove(existingDescriptor);
            services.AddSingleton<IWalletProvider>(sp =>
            {
                // Resolve the inner provider from the original descriptor
                var inner = (IWalletProvider)(existingDescriptor.ImplementationFactory?.Invoke(sp)
                    ?? (existingDescriptor.ImplementationInstance
                        ?? ActivatorUtilities.CreateInstance(sp, existingDescriptor.ImplementationType!)));

                var delegator = sp.GetRequiredService<IDelegatorProvider>();
                var transport = sp.GetRequiredService<IClientTransport>();
                return new DelegatingWalletProvider(inner, delegator, transport);
            });
        }

        services.AddSingleton<DelegationMonitorService>();
        services.AddHostedService(sp => sp.GetRequiredService<DelegationMonitorService>());

        return services;
    }

    /// <summary>
    /// Registers VTXO polling event handlers that automatically poll for VTXO updates
    /// after batch success and spend transactions.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Optional action to configure polling delays.</param>
    public static IServiceCollection AddVtxoPolling(this IServiceCollection services, Action<VtxoPollingOptions>? configureOptions = null)
    {
        // Configure options
        if (configureOptions is not null)
        {
            services.Configure(configureOptions);
        }
        else
        {
            services.Configure<VtxoPollingOptions>(options =>
            {
                options.BatchSuccessPollingDelay = TimeSpan.FromMilliseconds(500);
                options.TransactionBroadcastPollingDelay = TimeSpan.FromMilliseconds(500);
            });
        }

        // Register event handlers
        services.AddSingleton<PostBatchVtxoPollingHandler>();
        services.AddSingleton<IEventHandler<PostBatchSessionEvent>>(sp => sp.GetRequiredService<PostBatchVtxoPollingHandler>());

        services.AddSingleton<PostSpendVtxoPollingHandler>();
        services.AddSingleton<IEventHandler<PostCoinsSpendActionEvent>>(sp => sp.GetRequiredService<PostSpendVtxoPollingHandler>());

        return services;
    }
}
