using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using NArk.ArkadeIntents.Evm;
using NArk.ArkadeIntents.Rfq.Profiles.Evm;

namespace NArk.Wallet.Gateway;

/// <summary>Registers the server-only EVM chain adapters from a secret provider.</summary>
public static class EvmServerChainExample
{
    /// <summary>Registers singleton RPC, sender, and proof clients and clears the loaded key buffer.</summary>
    /// <param name="services">Server dependency injection collection.</param>
    /// <param name="endpoint">Absolute EVM JSON-RPC endpoint; URI userinfo does not configure authentication.</param>
    /// <param name="loadPrivateKey">Server secret callback returning an owned 32-byte buffer.</param>
    /// <param name="rpcOptions">Receipt and response limits.</param>
    /// <param name="senderOptions">Gas-payer address, fee caps, and gas cap.</param>
    /// <param name="policy">Expected chain, token, contract, and proof policy.</param>
    /// <param name="configureHttpClient">Optional authentication and transport configuration.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddEvmSwapChainExample(
        this IServiceCollection services,
        Uri endpoint,
        Func<IServiceProvider, byte[]> loadPrivateKey,
        EvmJsonRpcOptions rpcOptions,
        EvmTransactionSenderOptions senderOptions,
        EvmSendPolicy policy,
        Action<HttpClient>? configureHttpClient = null)
    {
        services.AddHttpClient("evm-swap", client =>
        {
            client.BaseAddress = endpoint;
            configureHttpClient?.Invoke(client);
        });
        services.AddSingleton(serviceProvider => new EvmJsonRpcClient(
            serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("evm-swap"),
            endpoint,
            rpcOptions));
        services.AddSingleton(serviceProvider =>
        {
            var privateKey = loadPrivateKey(serviceProvider)
                ?? throw new InvalidOperationException("the EVM secret provider returned no key");
            try
            {
                return new EvmLocalTransactionSender(
                    serviceProvider.GetRequiredService<EvmJsonRpcClient>(), privateKey, senderOptions);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(privateKey);
            }
        });
        services.AddSingleton(serviceProvider => new EvmSwapChainClient(
            serviceProvider.GetRequiredService<EvmJsonRpcClient>(),
            serviceProvider.GetRequiredService<EvmLocalTransactionSender>(),
            policy));
        return services;
    }
}
