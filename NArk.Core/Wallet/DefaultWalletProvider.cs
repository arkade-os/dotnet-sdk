using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Safety;
using NArk.Abstractions.Wallets;
using NArk.Core.Transport;
using NBitcoin;

namespace NArk.Core.Wallet;

/// <summary>
/// Default implementation of <see cref="IWalletProvider"/>.
/// <para>
/// Signing capability is answered by <em>asking</em> — never by tagging the wallet:
/// <see cref="GetSignerAsync"/> returns a local signer when <see cref="ArkWalletInfo.Secret"/>
/// is present, a remote-signer proxy when an <see cref="IRemoteSignerTransport"/> is registered
/// and claims the wallet, and <c>null</c> otherwise (watch-only). Address derivation always
/// works from <see cref="ArkWalletInfo.AccountDescriptor"/> alone, regardless of signer
/// availability.
/// </para>
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
            logger?.LogDebug("GetSignerAsync: identifier={Identifier}, walletId={WalletId}, walletType={WalletType}, hasSecret={HasSecret}",
                identifier, wallet.Id, wallet.WalletType, !string.IsNullOrEmpty(wallet.Secret));

            // Local signing material present → produce a local signer of the right shape.
            if (!string.IsNullOrEmpty(wallet.Secret))
            {
                return wallet.WalletType switch
                {
                    WalletType.HD => new HierarchicalDeterministicWalletSigner(wallet),
                    WalletType.SingleKey => NSecWalletSigner.FromNsec(wallet.Secret, logger),
                    _ => throw new ArgumentOutOfRangeException(nameof(wallet.WalletType))
                };
            }

            // No local secret → ask the remote-signer transport (if any) whether it can sign for
            // this wallet. The transport is the source of truth for "remote vs watch-only" —
            // no flag on ArkWalletInfo encodes it.
            if (remoteSignerTransport is not null
                && await remoteSignerTransport.KnowsWalletAsync(wallet.Id, cancellationToken).ConfigureAwait(false))
            {
                return new RemoteArkadeWalletSigner(wallet.Id, remoteSignerTransport);
            }

            // No local key, no remote signer claims it → watch-only.
            return null;
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

            // Cross-check the stored descriptor against the one derived from the local nsec —
            // only meaningful when we actually have the secret. Watch-only/remote single-key
            // wallets keep the stored AccountDescriptor verbatim.
            if (wallet.WalletType == WalletType.SingleKey && !string.IsNullOrEmpty(wallet.Secret))
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

            // Address derivation is a function of the descriptor shape, which the WalletType
            // already encodes — no string-sniff needed.
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
