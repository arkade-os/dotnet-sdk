using Microsoft.Extensions.DependencyInjection;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Composition;
using NArk.ArkadeIntents.Evm;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Onchain;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Evm;
using NArk.Core.Transport;
using NBitcoin;

namespace NArk.Wallet.Gateway;

internal static class ComposedEvmSettlementExample
{
    internal static IServiceCollection AddComposedEvmSettlementExample(
        this IServiceCollection services, EvmSendPolicy policy, ComposedSwapOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(policy);
        services.AddSingleton(policy);
        services.AddSingleton(serviceProvider => new ComposedSwapClient(
            serviceProvider.GetRequiredService<EvmIntentsClient>(),
            serviceProvider.GetRequiredService<LightningIntentsClient>(),
            serviceProvider.GetRequiredService<OnchainIntentsClient>(),
            options));
        services.AddSingleton(serviceProvider => new ComposedSwapExecutionClient(
            serviceProvider.GetRequiredService<IArkadeIntentStorage>(),
            serviceProvider.GetRequiredService<IVtxoStorage>(),
            serviceProvider.GetRequiredService<IBitcoinBlockchain>(),
            serviceProvider.GetRequiredService<IContractStorage>(),
            serviceProvider.GetRequiredService<IClientTransport>(),
            serviceProvider.GetRequiredService<LightningIntentsClient>(),
            serviceProvider.GetRequiredService<OnchainIntentsClient>(),
            serviceProvider.GetRequiredService<EvmJsonRpcClient>(),
            serviceProvider.GetRequiredService<EvmLocalTransactionSender>(),
            serviceProvider.GetRequiredService<EvmSendPolicy>()));
        return services;
    }

    internal static Task<ArkadeToEvmRoute> CreateArkadeRouteAsync(
        ComposedSwapClient composer, string walletId, long amountSats, string merchantEvmAddress,
        EvmSendPolicy policy, IRfqTransport outgoingRfq, CancellationToken cancellationToken = default) =>
        composer.CreateArkadeAsync(walletId, amountSats, merchantEvmAddress, policy, outgoingRfq,
            cancellationToken: cancellationToken);

    internal static Task<LightningToEvmRoute> CreateLightningRouteAsync(
        ComposedSwapClient composer, string walletId, long amountSats, string merchantEvmAddress,
        EvmSendPolicy policy, IRfqTransport outgoingRfq, IRfqTransport ingressRfq,
        string covclaimdPubkey, CancellationToken cancellationToken = default) =>
        composer.CreateLightningAsync(walletId, amountSats, merchantEvmAddress, policy, outgoingRfq,
            ingressRfq, covclaimdPubkey, cancellationToken: cancellationToken);

    internal static Task<OnchainToEvmRoute> CreateOnchainRouteAsync(
        ComposedSwapClient composer, string walletId, long amountSats, string merchantEvmAddress,
        EvmSendPolicy policy, IRfqTransport outgoingRfq, IRfqTransport ingressRfq,
        string covclaimdPubkey, BitcoinAddress refundAddress, CancellationToken cancellationToken = default) =>
        composer.CreateOnchainAsync(walletId, amountSats, merchantEvmAddress, policy, outgoingRfq,
            ingressRfq, covclaimdPubkey, refundAddress, cancellationToken: cancellationToken);

    internal static Task<ComposedSwapExecutionResult> AdvanceAsync(
        ComposedSwapExecutionClient executor, string outgoingRfqId, string? ingressRfqId = null,
        CancellationToken cancellationToken = default) =>
        executor.AdvanceAsync(outgoingRfqId, ingressRfqId, cancellationToken);
}
