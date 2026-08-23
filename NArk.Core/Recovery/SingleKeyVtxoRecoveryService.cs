using Microsoft.Extensions.Logging;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Recovery;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Transport;

namespace NArk.Core.Recovery;

/// <summary>
/// Recovery for SingleKey wallets. There is no derivation index to scan: the
/// flat tr(pubkey) descriptor yields one candidate set. We probe every
/// <see cref="IContractDiscoveryProvider"/> ONCE with that descriptor; the
/// indexer provider internally probes { current signer ∪ deprecated signers }
/// (IndexerVtxoDiscoveryProvider.BuildCandidates), so funds stranded under a
/// rotated signer are discovered. Discovered contracts are persisted Active.
/// </summary>
public class SingleKeyVtxoRecoveryService(
    IEnumerable<IContractDiscoveryProvider> providers,
    IWalletStorage walletStorage,
    IContractStorage contractStorage,
    IClientTransport clientTransport,
    ILogger<SingleKeyVtxoRecoveryService>? logger = null) : ISingleKeyDefaultEnsurer
{
    public async Task<int> DiscoverAsync(string walletId, CancellationToken cancellationToken = default)
    {
        var wallet = await walletStorage.GetWalletById(walletId, cancellationToken)
            ?? throw new InvalidOperationException($"Wallet '{walletId}' not found.");
        if (wallet.WalletType != WalletType.SingleKey)
            throw new InvalidOperationException(
                $"SingleKeyVtxoRecoveryService only supports SingleKey wallets; '{walletId}' is {wallet.WalletType}.");
        if (string.IsNullOrEmpty(wallet.AccountDescriptor))
            throw new InvalidOperationException($"SingleKey wallet '{walletId}' has no AccountDescriptor.");

        var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
        var descriptor = KeyExtensions.ParseOutputDescriptor(wallet.AccountDescriptor!, serverInfo.Network);

        var providerList = providers.Where(p => p is not NullContractDiscoveryProvider).ToList();
        var discovered = new Dictionary<string, ArkContract>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in providerList)
        {
            DiscoveryResult result;
            try
            {
                result = await provider.DiscoverAsync(wallet, descriptor, index: 0, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "SingleKey recovery: provider {Provider} threw; skipping", provider.Name);
                continue;
            }
            if (!result.Used) continue;
            foreach (var contract in result.Contracts)
                discovered.TryAdd(contract.GetScriptPubKey().ToHex(), contract);
        }

        var persisted = 0;
        foreach (var (script, contract) in discovered)
        {
            var entity = contract.ToEntity(wallet.Id, serverInfo.SignerKey, activityState: ContractActivityState.Active) with
            {
                Metadata = new Dictionary<string, string> { ["Source"] = "recovery:singlekey" },
            };
            try
            {
                await contractStorage.SaveContract(entity, cancellationToken);
                persisted++;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "SingleKey recovery: failed to persist {Script}", script);
            }
        }
        logger?.LogInformation("SingleKey recovery for {WalletId}: persisted {Count} contract(s)", walletId, persisted);
        return persisted;
    }

    /// <summary>
    /// Idempotently ensures the SingleKey wallet's CURRENT-signer default contract exists
    /// (Active, <c>Source="Default"</c>). The canonical default is the <see cref="ArkPaymentContract"/>
    /// under the current <c>ArkServerInfo.SignerKey</c> — the very contract StoreOverview / the
    /// dashboard render. We build it DIRECTLY rather than via the SendToSelf provider path, because
    /// <see cref="Wallet.SingleKeyAddressProvider.GetNextContract"/> redirects SendToSelf to the
    /// sweep <c>Destination</c> (as an Inactive <c>UnknownArkContract</c>) when one is configured;
    /// going through that path would return the destination's script and let the reconciler
    /// deactivate the genuine payment default. Persisting upserts on {Script, WalletId}, so this is
    /// a no-op when the current default already exists, and after a signer rotation it creates the
    /// new-signer default. Deactivating the stale old-signer default is the reconciliation
    /// service's job, not this one.
    /// </summary>
    /// <returns>The script hex of the ensured current-signer Default contract.</returns>
    public async Task<string> EnsureDefaultAsync(string walletId, CancellationToken cancellationToken = default)
    {
        var wallet = await walletStorage.GetWalletById(walletId, cancellationToken)
            ?? throw new InvalidOperationException($"Wallet '{walletId}' not found.");
        if (wallet.WalletType != WalletType.SingleKey)
            throw new InvalidOperationException(
                $"SingleKeyVtxoRecoveryService only supports SingleKey wallets; '{walletId}' is {wallet.WalletType}.");
        if (string.IsNullOrEmpty(wallet.AccountDescriptor))
            throw new InvalidOperationException($"SingleKey wallet '{walletId}' has no AccountDescriptor.");

        var info = await clientTransport.GetServerInfoAsync(cancellationToken);
        var descriptor = KeyExtensions.ParseOutputDescriptor(wallet.AccountDescriptor!, info.Network);
        // Canonical default = the payment contract under the CURRENT signer, built directly so a
        // configured sweep Destination cannot redirect it (the SendToSelf provider path does that).
        var contract = new ArkPaymentContract(info.SignerKey, info.UnilateralExit, descriptor);
        var entity = contract.ToEntity(walletId, info.SignerKey, activityState: ContractActivityState.Active) with
        {
            Metadata = new Dictionary<string, string> { ["Source"] = "Default" },
        };
        await contractStorage.SaveContract(entity, cancellationToken);
        return contract.GetScriptPubKey().ToHex();
    }
}
