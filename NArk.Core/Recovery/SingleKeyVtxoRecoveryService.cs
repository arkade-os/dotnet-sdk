using Microsoft.Extensions.Logging;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Recovery;
using NArk.Abstractions.Wallets;
using NArk.Core.Services;
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
    IContractService contractService,
    IClientTransport clientTransport,
    ILogger<SingleKeyVtxoRecoveryService>? logger = null)
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
    /// (Active, Source="Default"). DeriveContract derives from the current ArkServerInfo.SignerKey
    /// and upserts on {Script, WalletId}, so this is a no-op when the current default already
    /// exists, and after a signer rotation it creates the new-signer default. Deactivating the
    /// stale old-signer default is the plugin's reconciliation job, not this one.
    /// </summary>
    public async Task EnsureDefaultAsync(string walletId, CancellationToken cancellationToken = default)
    {
        var wallet = await walletStorage.GetWalletById(walletId, cancellationToken)
            ?? throw new InvalidOperationException($"Wallet '{walletId}' not found.");
        if (wallet.WalletType != WalletType.SingleKey)
            throw new InvalidOperationException(
                $"SingleKeyVtxoRecoveryService only supports SingleKey wallets; '{walletId}' is {wallet.WalletType}.");

        await contractService.DeriveContract(
            walletId,
            NextContractPurpose.SendToSelf,
            ContractActivityState.Active,
            metadata: new Dictionary<string, string> { ["Source"] = "Default" },
            cancellationToken: cancellationToken);
    }
}
