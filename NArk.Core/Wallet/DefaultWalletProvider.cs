using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Safety;
using NArk.Abstractions.Wallets;
using NArk.Core.Transport;
using NArk.Core.Wallet.SigningSources;
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
    // Signer instances must be reused across calls so the MuSig2 secret-nonce store on each
    // signing source (populated by GenerateNonces, consumed by SignMusig — see
    // IArkadeWalletSigner) survives between the two calls. Keyed by wallet.Id only — if a
    // wallet is re-imported with different signing material the caller must explicitly
    // invalidate (restart the host, or call into a fresh provider instance).
    private readonly ConcurrentDictionary<string, IArkadeWalletSigner> _signerCache = new();

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

            // Hot-path short-circuit: this method is called per-VTXO during batch participation,
            // so avoid constructing a fresh Bip39SigningSource (master-fingerprint derivation)
            // and round-tripping KnowsWalletAsync (potentially a network call for remote
            // transports) every time. Cache miss runs the full composition below.
            if (_signerCache.TryGetValue(wallet.Id, out var cached))
                return cached;

            var sources = new List<IDescriptorSigningSource>();

            // Local signing material present → add the matching local signing source.
            if (!string.IsNullOrEmpty(wallet.Secret))
            {
                sources.Add(wallet.WalletType switch
                {
                    WalletType.HD => new Bip39SigningSource(wallet.Secret),
                    WalletType.SingleKey => NsecSigningSource.FromNsec(wallet.Secret, logger),
                    _ => throw new ArgumentOutOfRangeException(nameof(wallet.WalletType))
                });
            }

            // Remote-signer transport claims this wallet → add a remote source as a fallback
            // for descriptors no local source covers. Order is significant: local sources get
            // first refusal.
            if (remoteSignerTransport is not null
                && await remoteSignerTransport.KnowsWalletAsync(wallet.Id, cancellationToken).ConfigureAwait(false))
            {
                sources.Add(new RemoteTransportSigningSource(remoteSignerTransport, wallet.Id));
            }

            // Nothing can sign for this wallet → watch-only.
            if (sources.Count == 0)
                return null;

            return _signerCache.GetOrAdd(wallet.Id, _ => new CompositeArkadeWalletSigner(sources));
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
