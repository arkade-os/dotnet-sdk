using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.Scripts;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Events;
using NArk.Core.Models.Options;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models;

namespace NArk.Hosting;

public static class AppExtensions
{
    public static ArkApplicationBuilder AddArk(this IHostBuilder builder)
    {
        return new ArkApplicationBuilder(builder);
    }

    public class ArkApplicationBuilder : IHostBuilder
    {
        private readonly IHostBuilder _hostBuilder;

        internal ArkApplicationBuilder(IHostBuilder hostBuilder)
        {
            hostBuilder.ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddArkCoreServices();
            });
            _hostBuilder = hostBuilder;
        }

        public ArkApplicationBuilder WithSweeperForceRefreshInterval(TimeSpan interval)
        {
            _hostBuilder.ConfigureServices(services =>
                services.Configure<SweeperServiceOptions>(o => { o.ForceRefreshInterval = interval; }));

            return this;
        }

        public ArkApplicationBuilder WithSafetyService<TSafety>() where TSafety : class, ISafetyService
        {
            _hostBuilder.ConfigureServices(services => { services.AddSingleton<ISafetyService, TSafety>(); });
            return this;
        }

        public ArkApplicationBuilder WithVtxoStorage<TStorage>() where TStorage : class, IVtxoStorage
        {
            _hostBuilder.ConfigureServices(services =>
            {
                services.AddSingleton<IVtxoStorage, TStorage>();
                services.AddSingleton<IActiveScriptsProvider>(provider => provider.GetRequiredService<IVtxoStorage>());
            });
            return this;
        }

        public ArkApplicationBuilder WithWalletProvider<TProvider>() where TProvider : class, IWalletProvider
        {
            _hostBuilder.ConfigureServices(services =>
            {
                services.AddSingleton<TProvider>();
                services.AddSingleton<IWalletProvider, TProvider>(p => p.GetRequiredService<TProvider>());
            });

            return this;
        }

        public ArkApplicationBuilder WithIntentStorage<TStorage>() where TStorage : class, IIntentStorage
        {
            _hostBuilder.ConfigureServices(services => { services.AddSingleton<IIntentStorage, TStorage>(); });
            return this;
        }

        public ArkApplicationBuilder WithSwapStorage<TStorage>() where TStorage : class, ISwapStorage
        {
            _hostBuilder.ConfigureServices(services => { services.AddSingleton<ISwapStorage, TStorage>(); });
            return this;
        }

        public ArkApplicationBuilder WithContractStorage<TStorage>() where TStorage : class, IContractStorage
        {
            _hostBuilder.ConfigureServices(services =>
            {
                services.AddSingleton<IContractStorage, TStorage>();
                services.AddSingleton<IActiveScriptsProvider>(provider => provider.GetRequiredService<IContractStorage>());
            });
            return this;
        }

        public ArkApplicationBuilder WithIntentScheduler<TScheduler>() where TScheduler : class, IIntentScheduler
        {
            _hostBuilder.ConfigureServices(services => { services.AddSingleton<IIntentScheduler, TScheduler>(); });
            return this;
        }

        public ArkApplicationBuilder WithTimeProvider<TTime>() where TTime : class, IChainTimeProvider
        {
            _hostBuilder.ConfigureServices(services => { services.AddSingleton<IChainTimeProvider, TTime>(); });
            return this;
        }

        public ArkApplicationBuilder WithEventHandler<TEvent, THandler>()
            where TEvent : class
            where THandler : class, IEventHandler<TEvent>
        {
            _hostBuilder.ConfigureServices(services =>
            {
                services.AddTransient<IEventHandler<TEvent>, THandler>();
            });
            return this;
        }

        /// <summary>
        /// Configures VTXO polling options. VTXO polling is enabled by default via AddArkCoreServices.
        /// Use this method only if you need to customize the polling delays.
        /// </summary>
        /// <param name="configureOptions">Action to configure polling delays.</param>
        public ArkApplicationBuilder ConfigureVtxoPolling(Action<VtxoPollingOptions> configureOptions)
        {
            _hostBuilder.ConfigureServices(services => services.Configure(configureOptions));
            return this;
        }

        public ArkApplicationBuilder OnMainnet()
        {
            _hostBuilder.ConfigureServices(services => services.AddArkNetwork(ArkNetworkConfig.Mainnet));
            return this;
        }

        public ArkApplicationBuilder OnRegtest()
        {
            _hostBuilder.ConfigureServices(services => services.AddArkNetwork(ArkNetworkConfig.Regtest));
            return this;
        }

        public ArkApplicationBuilder OnMutinynet()
        {
            _hostBuilder.ConfigureServices(services => services.AddArkNetwork(ArkNetworkConfig.Mutinynet));
            return this;
        }

        public ArkApplicationBuilder OnNetwork(ArkNetworkConfig config)
        {
            _hostBuilder.ConfigureServices(services => services.AddArkNetwork(config));
            return this;
        }

        public ArkApplicationBuilder OnCustomGrpcArk(string arkUrl)
        {
            _hostBuilder.ConfigureServices(services =>
                services.AddArkNetwork(new ArkNetworkConfig(arkUrl), configureBoltz: false));
            return this;
        }

        public ArkApplicationBuilder OnCustomBoltz(string boltzUrl, string? websocketUrl)
        {
            _hostBuilder.ConfigureServices(services =>
            {
                services.Configure<BoltzClientOptions>(b =>
                {
                    b.BoltzUrl = boltzUrl;
                    b.WebsocketUrl = websocketUrl ?? boltzUrl;
                });
            });
            return EnableSwaps();
        }

        public ArkApplicationBuilder EnableSwaps(Action<BoltzClientOptions>? boltzOptionsConfigure = null)
        {
            _hostBuilder.ConfigureServices(services =>
            {
                if (boltzOptionsConfigure != null)
                {
                    services.Configure(boltzOptionsConfigure);
                }

                services.AddHttpClient<BoltzClient>();
                services.AddArkSwapServices();
            });
            return this;
        }

        public IHostBuilder ConfigureHostConfiguration(Action<IConfigurationBuilder> configureDelegate)
        {
            return _hostBuilder.ConfigureHostConfiguration(configureDelegate);
        }

        public IHostBuilder ConfigureAppConfiguration(
            Action<HostBuilderContext, IConfigurationBuilder> configureDelegate)
        {
            return _hostBuilder.ConfigureAppConfiguration(configureDelegate);
        }

        public IHostBuilder ConfigureServices(Action<HostBuilderContext, IServiceCollection> configureDelegate)
        {
            return _hostBuilder.ConfigureServices(configureDelegate);
        }

        public IHostBuilder UseServiceProviderFactory<TContainerBuilder>(
            IServiceProviderFactory<TContainerBuilder> factory) where TContainerBuilder : notnull
        {
            return _hostBuilder.UseServiceProviderFactory(factory);
        }

        public IHostBuilder UseServiceProviderFactory<TContainerBuilder>(
            Func<HostBuilderContext, IServiceProviderFactory<TContainerBuilder>> factory)
            where TContainerBuilder : notnull
        {
            return _hostBuilder.UseServiceProviderFactory(factory);
        }

        public IHostBuilder ConfigureContainer<TContainerBuilder>(
            Action<HostBuilderContext, TContainerBuilder> configureDelegate)
        {
            return _hostBuilder.ConfigureContainer(configureDelegate);
        }

        public IHost Build()
        {
            return _hostBuilder.Build();
        }

        public IDictionary<object, object> Properties => _hostBuilder.Properties;
    }
}