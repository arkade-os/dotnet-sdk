using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using NArk.Abstractions.Blockchain;
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
using NArk.Abstractions.VirtualTxs;
using NArk.Abstractions.Exit;
using NArk.Core.Exit;
using NArk.Core.VirtualTxs;

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
    string? ExplorerUri = null,

    /// <summary>
    /// Default Esplora REST API endpoint for this network. Used by
    /// <see cref="NArk.Core.Blockchain.EsploraBlockchain"/> when an app
    /// needs an <see cref="NArk.Abstractions.Blockchain.IBitcoinBlockchain"/>
    /// without running its own NBXplorer / bitcoind. Values mirror the
    /// per-network defaults shipped by the canonical Arkade ts-sdk.
    /// </summary>
    [property: JsonPropertyName("esplora")]
    string? EsploraUri = null,

    /// <summary>
    /// Default Electrum websocket endpoint for this network (mirrors the
    /// ts-sdk's <c>ELECTRUM_WS_URL</c>). Optional — only consumed by clients
    /// that want a websocket-driven Electrum chain source.
    /// </summary>
    [property: JsonPropertyName("electrum-ws")]
    string? ElectrumWsUri = null,

    /// <summary>
    /// Default Electrum endpoint hostname for raw-TCP consumers (mirrors
    /// the ts-sdk's <c>ELECTRUM_TCP_HOST</c>; hostname only, no port — the
    /// consumer picks the port: 50001 plain TCP, 50002 TCP+TLS, 50004 WSS
    /// on the public Ark Labs Fulcrum instances). Optional and informational
    /// — no built-in NArk service consumes it; .NET clients that speak
    /// Electrum-over-TCP directly can pull it off the preset.
    /// </summary>
    [property: JsonPropertyName("electrum-tcp")]
    string? ElectrumTcpHost = null)
{
    /// <summary>Mainnet configuration.</summary>
    public static readonly ArkNetworkConfig Mainnet = new(
        ArkUri: "https://arkade.computer",
        ArkadeWalletUri: "https://arkade.money",
        BoltzUri: "https://api.boltz.exchange/",
        ExplorerUri: "https://arkade.space",
        EsploraUri: "https://mempool.arkade.sh/api",
        ElectrumWsUri: "wss://electrum.arkade.sh",
        ElectrumTcpHost: "electrum.arkade.sh");

    /// <summary>Mutinynet (signet) configuration.</summary>
    public static readonly ArkNetworkConfig Mutinynet = new(
        ArkUri: "https://mutinynet.arkade.sh",
        ArkadeWalletUri: "https://mutinynet.arkade.money",
        BoltzUri: "https://api.boltz.mutinynet.arkade.sh/",
        ExplorerUri: "https://explorer.mutinynet.arkade.sh",
        EsploraUri: "https://mempool.mutinynet.arkade.sh/api",
        ElectrumWsUri: "wss://electrum.mutinynet.arkade.sh",
        ElectrumTcpHost: "electrum.mutinynet.arkade.sh");

    /// <summary>Local regtest configuration.</summary>
    public static readonly ArkNetworkConfig Regtest = new(
        ArkUri: "http://localhost:7070",
        ArkadeWalletUri: "http://localhost:3002",
        BoltzUri: "http://localhost:9069/",
        ExplorerUri: "http://localhost:7080",
        // ts-sdk regtest Esplora default: a local mempool/Chopsticks deployment.
        EsploraUri: "http://localhost:3000",
        // Regtest WS bridge convention: electrum-ws on port 50003 (ts-sdk).
        ElectrumWsUri: "ws://localhost:50003",
        ElectrumTcpHost: "localhost");

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
    /// ISwapStorage, IWallet, ISafetyService, IBitcoinBlockchain, and IClientTransport.
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
        services.AddSingleton<PendingArkTransactionRecoveryService>();
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
        // BoardingUtxoDiscoveryProvider activates only when an IBitcoinBlockchain has
        // been registered (typically by the plugin via NBXplorer/Esplora). When absent the
        // provider is a no-op so HD recovery still works without on-chain probing.
        services.AddSingleton<IContractDiscoveryProvider>(sp =>
            sp.GetService<IBitcoinBlockchain>() is { } utxoProvider
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

    /// <summary>
    /// Registers unilateral exit services including virtual tx management, exit orchestration,
    /// and watchtower monitoring. Caller must still register IBitcoinBlockchain, IVirtualTxStorage,
    /// and IExitSessionStorage.
    /// </summary>
    /// <remarks>
    /// Auto-fetching the virtual-tx chain on every VTXO arrival is OPT-IN —
    /// call <see cref="AddVirtualTxAutoFetch"/> separately if the host wants
    /// chains pre-stored ahead of any potential exit. Without it,
    /// <see cref="UnilateralExitService.StartExitAsync"/> still fetches the
    /// chain on demand via <see cref="VirtualTxService.EnsureHexPopulatedAsync"/>.
    /// </remarks>
    public static IServiceCollection AddUnilateralExit(
        this IServiceCollection services,
        Action<VirtualTxOptions>? configureVirtualTx = null,
        Action<ExitWatchtowerOptions>? configureWatchtower = null)
    {
        // Configure options
        services.Configure(configureVirtualTx ?? (_ => { }));
        services.Configure(configureWatchtower ?? (_ => { }));

        // Core services
        services.AddSingleton<VirtualTxService>();
        services.AddSingleton<UnilateralExitService>();
        services.AddSingleton<ExitWatchtowerService>();

        // Prune-on-spend handler is registered automatically — it's a
        // pure cleanup pass that's safe regardless of whether auto-fetch
        // is enabled (no-op if there's nothing stored to prune).
        services.AddSingleton<PostSpendVirtualTxPruneHandler>();
        services.AddSingleton<IEventHandler<PostCoinsSpendActionEvent>>(
            sp => sp.GetRequiredService<PostSpendVirtualTxPruneHandler>());

        return services;
    }

    /// <summary>
    /// Opt-in: subscribes to <see cref="IVtxoStorage.VtxosChanged"/> and
    /// fetches the virtual-tx chain for every new VTXO the wallet receives,
    /// regardless of source (batch settlement, change from a spend, incoming
    /// payment, swap claim, sweep, …). With this the wallet always has exit
    /// data ready locally; without it, the chain is fetched lazily on the
    /// first <see cref="UnilateralExitService.StartExitAsync"/> call for
    /// each VTXO.
    /// </summary>
    /// <remarks>
    /// Storage cost scales with the wallet's VTXO count — for a wallet that
    /// rarely exits unilaterally, deferring the fetch can be the right call.
    /// Call after <see cref="AddUnilateralExit"/>.
    /// </remarks>
    public static IServiceCollection AddVirtualTxAutoFetch(this IServiceCollection services)
    {
        services.AddSingleton<VtxoChainAutoFetchService>();
        services.AddHostedService(sp => sp.GetRequiredService<VtxoChainAutoFetchService>());
        return services;
    }

    /// <summary>
    /// Registers the exit watchtower as a background service for autonomous monitoring.
    /// Call after <see cref="AddUnilateralExit"/>.
    /// </summary>
    public static IServiceCollection AddExitWatchtowerBackgroundService(this IServiceCollection services)
    {
        services.AddHostedService<ExitWatchtowerBackgroundService>();
        return services;
    }

    /// <summary>
    /// Registers in-process implementations of <see cref="IExitSessionStorage"/>
    /// and <see cref="IVirtualTxStorage"/>. State is kept in <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>s
    /// and lives only for the lifetime of the host — no schema, no migrations,
    /// no SQL. Lost on process restart.
    /// <para>
    /// Pair with <see cref="AddUnilateralExit"/> when the consumer wants the
    /// stateful flow (idempotent re-invocation, watchtower visibility) but
    /// doesn't want the EF Core schema cost — e.g. recovery-tooling CLIs,
    /// plugins, ephemeral wallets. For stateless one-shot exits without
    /// any storage at all (durable or in-memory), use
    /// <c>UnilateralExitService.BroadcastExitChainAsync</c> /
    /// <c>ClaimMaturedExitAsync</c> instead.
    /// </para>
    /// </summary>
    public static IServiceCollection AddInMemoryExitStorage(this IServiceCollection services)
    {
        services.AddSingleton<IExitSessionStorage, InMemoryExitSessionStorage>();
        services.AddSingleton<IVirtualTxStorage, InMemoryVirtualTxStorage>();
        return services;
    }
}
