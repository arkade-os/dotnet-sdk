using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Safety;
using NArk.Abstractions.Wallets;
using NArk.Core.Transport;
using NBitcoin;

namespace NArk.Core.Wallet;

/// <summary>
/// Default implementation of IWalletProvider using SDK wallet infrastructure.
/// </summary>
public class DefaultWalletProvider(
    IClientTransport clientTransport,
    ISafetyService safetyService,
    IWalletStorage walletStorage,
    IContractStorage contractStorage,
    ILogger<DefaultWalletProvider>? logger = null,
    IRemoteSignerTransport? remoteSignerTransport = null)
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
                WalletType.SingleKey => NSecWalletSigner.FromNsec(
                    wallet.Secret ?? throw new InvalidOperationException(
                        $"SingleKey wallet '{wallet.Id}' has no Secret — nsec is required for SingleKey wallets."),
                    logger),
                WalletType.WatchOnly => null,
                WalletType.Remote => CreateRemoteSigner(wallet),
                _ => throw new ArgumentOutOfRangeException(nameof(wallet.WalletType))
            };
        }
        catch (KeyNotFoundException)
        {
            logger?.LogWarning("GetSignerAsync: wallet not found for identifier={Identifier}", identifier);
            return null;
        }
    }

    private IArkadeWalletSigner CreateRemoteSigner(ArkWalletInfo wallet)
    {
        if (remoteSignerTransport is null)
        {
            throw new InvalidOperationException(
                $"Remote wallet '{wallet.Id}' selected but no IRemoteSignerTransport was registered. " +
                "Register one in DI before using WalletType.Remote.");
        }

        return new RemoteArkadeWalletSigner(wallet.Id, remoteSignerTransport);
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
                var secret = wallet.Secret ?? throw new InvalidOperationException(
                    $"SingleKey wallet '{wallet.Id}' has no Secret — nsec is required for SingleKey wallets.");
                var derivedDescriptor = WalletFactory.GetOutputDescriptorFromNsec(secret);
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
                // WatchOnly and Remote wallets pick their address provider by descriptor shape:
                // an HD-style account descriptor (tr([fp/path]xpub/0/*)) routes to the HD
                // provider so new indices can be derived; a bare tr(pubkey) routes to the
                // single-key provider so the one address is reused.
                WalletType.WatchOnly => CreateWatchOnlyOrRemoteAddressProvider(wallet, network, sweepDestination),
                WalletType.Remote => CreateWatchOnlyOrRemoteAddressProvider(wallet, network, sweepDestination),
                _ => throw new ArgumentOutOfRangeException(nameof(wallet.WalletType))
            };
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    private IArkadeAddressProvider CreateWatchOnlyOrRemoteAddressProvider(
        ArkWalletInfo wallet,
        Network network,
        ArkAddress? sweepDestination)
    {
        if (string.IsNullOrEmpty(wallet.AccountDescriptor))
        {
            throw new InvalidOperationException(
                $"Wallet '{wallet.Id}' (type={wallet.WalletType}) has no AccountDescriptor. " +
                "WatchOnly and Remote wallets require an AccountDescriptor — a tr(pubkey) for single-key style " +
                "or a tr([fingerprint/path]xpub/0/*) for hierarchical-deterministic style.");
        }

        // HD account descriptors contain a derivation suffix ("/0/*"); single-key
        // descriptors are bare tr(pubkey) with no '*'.
        var isHd = wallet.AccountDescriptor.Contains('*');
        return isHd
            ? new HierarchicalDeterministicAddressProvider(clientTransport, safetyService, walletStorage, contractStorage, wallet, network, sweepDestination)
            : new SingleKeyAddressProvider(clientTransport, wallet, network, sweepDestination, logger);
    }
}
