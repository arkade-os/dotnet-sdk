using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using NArk.Abstractions.Fees;
using NArk.Core.CoinSelector;
using NArk.Core.Events;
using NArk.Core.Fees;
using NArk.Core.Models.Options;
using NArk.Core.Services;
using NArk.Core.Sweeper;
using NArk.Core.Transformers;
using NArk.Core.Transport;
using NArk.Transport.GrpcClient;
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

    [property: JsonPropertyName("boltz-ws")]
    string? BoltzWsUri = null,

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
        BoltzUri: "http://localhost:9001/",
        BoltzWsUri: "http://localhost:9004/",
        ExplorerUri: null);

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

        // VTXO polling - automatically poll for updates after batch success and spend transactions
        services.AddVtxoPolling();

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
