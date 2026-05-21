using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Safety;
using NArk.Abstractions.Wallets;
using NArk.Core.Transport;

namespace NArk.Core.Wallet;

/// <summary>
/// Default implementation of IWalletProvider using SDK wallet infrastructure.
/// </summary>
public class DefaultWalletProvider(
    IClientTransport clientTransport,
    ISafetyService safetyService,
    IWalletStorage walletStorage,
    IContractStorage contractStorage,
    ILogger<DefaultWalletProvider>? logger = null)
    : IWalletProvider
{
    public async Task<IArkadeWalletSigner?> GetSignerAsync(string identifier, CancellationToken cancellationToken = default)
    {
        try
        {
            // TEMP latency probe.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var wallet = await walletStorage.LoadWallet(identifier, cancellationToken);
            logger?.LogTrace("[wallet-probe] GetSigner LoadWallet: {Ms}ms", sw.ElapsedMilliseconds);
            logger?.LogDebug("GetSignerAsync: identifier={Identifier}, walletId={WalletId}, walletType={WalletType}, accountDescriptor={AccountDescriptor}",
                identifier, wallet.Id, wallet.WalletType, wallet.AccountDescriptor);
            return wallet.WalletType switch
            {
                WalletType.HD => new HierarchicalDeterministicWalletSigner(wallet),
                WalletType.SingleKey => NSecWalletSigner.FromNsec(wallet.Secret, logger),
                _ => throw new ArgumentOutOfRangeException(nameof(wallet.WalletType))
            };
        }
        catch (KeyNotFoundException)
        {
            logger?.LogWarning("GetSignerAsync: wallet not found for identifier={Identifier}", identifier);
            return null;
        }
    }

    public async Task<IArkadeAddressProvider?> GetAddressProviderAsync(string identifier, CancellationToken cancellationToken = default)
    {
        try
        {
            // TEMP latency probe.
            var swInfo = System.Diagnostics.Stopwatch.StartNew();
            var network = (await clientTransport.GetServerInfoAsync(cancellationToken)).Network;
            var infoMs = swInfo.ElapsedMilliseconds;
            var swLoad = System.Diagnostics.Stopwatch.StartNew();
            var wallet = await walletStorage.LoadWallet(identifier, cancellationToken);
            logger?.LogTrace("[wallet-probe] GetAddressProvider: GetServerInfo={InfoMs}ms LoadWallet={LoadMs}ms",
                infoMs, swLoad.ElapsedMilliseconds);
            ArkAddress? sweepDestination = null;
            if (!string.IsNullOrEmpty(wallet.Destination))
            {
                sweepDestination = ArkAddress.Parse(wallet.Destination);
            }
            if (wallet.WalletType == WalletType.SingleKey)
            {
                var derivedDescriptor = WalletFactory.GetOutputDescriptorFromNsec(wallet.Secret);
                if (wallet.AccountDescriptor != derivedDescriptor)
                {
                    logger?.LogWarning(
                        "SingleKey wallet {WalletId} stored descriptor mismatch — using derived. stored={StoredDescriptor}, derived={DerivedDescriptor}",
                        wallet.Id, wallet.AccountDescriptor, derivedDescriptor);
                    wallet = wallet with { AccountDescriptor = derivedDescriptor };
                }
            }

            return wallet.WalletType switch
            {
                WalletType.HD => new HierarchicalDeterministicAddressProvider(clientTransport, safetyService, walletStorage, contractStorage, wallet, network, sweepDestination),
                WalletType.SingleKey => new SingleKeyAddressProvider(clientTransport, wallet, network, sweepDestination, logger),
                _ => throw new ArgumentOutOfRangeException(nameof(wallet.WalletType))
            };
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }
}
