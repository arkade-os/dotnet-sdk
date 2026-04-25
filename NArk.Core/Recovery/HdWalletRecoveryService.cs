using Microsoft.Extensions.Logging;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Recovery;
using NArk.Abstractions.Wallets;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Core.Recovery;

/// <summary>
/// Iteratively probes derivation indices of an HD wallet to recover contracts
/// that were used before the wallet was imported into local storage. Each
/// index is probed by every registered <see cref="IContractDiscoveryProvider"/>
/// (arkd indexer, on-chain boarding, boltz, …) and the union of results
/// determines whether the index counts as used.
/// </summary>
/// <remarks>
/// The scan stops once <see cref="RecoveryOptions.GapLimit"/> consecutive
/// unused indices are seen, or once <see cref="RecoveryOptions.MaxIndex"/> is
/// reached. Discovered contracts are persisted via <see cref="IContractStorage"/>
/// and <c>wallet.LastUsedIndex</c> is bumped to <c>HighestUsedIndex + 1</c> so
/// future derivations don't collide with recovered scripts.
/// </remarks>
public class HdWalletRecoveryService(
    IEnumerable<IContractDiscoveryProvider> providers,
    IWalletStorage walletStorage,
    IContractStorage contractStorage,
    IClientTransport clientTransport,
    ILogger<HdWalletRecoveryService>? logger = null)
{
    /// <summary>
    /// Scan an HD wallet's derivation indices for prior usage.
    /// </summary>
    /// <param name="walletId">The wallet to recover. MUST be a HD wallet.</param>
    /// <param name="options">Scan configuration. Defaults to <see cref="RecoveryOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="RecoveryReport"/> describing what was found.</returns>
    /// <exception cref="InvalidOperationException">
    /// If the wallet is not HD, has no AccountDescriptor, or is unknown.
    /// </exception>
    public async Task<RecoveryReport> ScanAsync(
        string walletId,
        RecoveryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= RecoveryOptions.Default;

        var wallet = await walletStorage.GetWalletById(walletId, cancellationToken)
            ?? throw new InvalidOperationException($"Wallet '{walletId}' not found.");
        if (wallet.WalletType != WalletType.HD)
            throw new InvalidOperationException(
                $"Recovery only supports HD wallets; '{walletId}' is {wallet.WalletType}.");
        if (string.IsNullOrEmpty(wallet.AccountDescriptor))
            throw new InvalidOperationException($"Wallet '{walletId}' has no AccountDescriptor.");

        var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
        var network = serverInfo.Network;
        // Drop any no-op placeholders (e.g. NullContractDiscoveryProvider that the
        // DI graph injects when an optional source like IBoardingUtxoProvider is missing).
        var providersList = providers
            .Where(p => p is not NullContractDiscoveryProvider)
            .ToList();
        if (providersList.Count == 0)
        {
            logger?.LogWarning("HdWalletRecoveryService: no IContractDiscoveryProvider registered — scan will be a no-op");
        }

        logger?.LogInformation(
            "Recovery scan starting for wallet {WalletId} with gap={Gap}, maxIndex={Max}, providers=[{Providers}]",
            walletId, options.GapLimit, options.MaxIndex,
            string.Join(", ", providersList.Select(p => p.Name)));

        var discoveredContracts = new List<DiscoveredContract>();
        var providerHits = providersList.ToDictionary(p => p.Name, _ => 0);
        var highestUsed = -1;
        var consecutiveMisses = 0;
        var scanned = 0;

        for (var index = options.StartIndex; index <= options.MaxIndex; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scanned++;

            var descriptor = DeriveAtIndex(wallet.AccountDescriptor, index, network);

            var indexUsed = false;
            foreach (var provider in providersList)
            {
                DiscoveryResult result;
                try
                {
                    result = await provider.DiscoverAsync(wallet, descriptor, index, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex,
                        "Recovery: provider {Provider} threw at index {Index}; treating as not-found",
                        provider.Name, index);
                    continue;
                }

                if (!result.Used) continue;

                indexUsed = true;
                providerHits[provider.Name]++;
                foreach (var contract in result.Contracts)
                {
                    discoveredContracts.Add(new DiscoveredContract(index, provider.Name, contract));
                }
            }

            if (indexUsed)
            {
                highestUsed = index;
                consecutiveMisses = 0;
                logger?.LogInformation(
                    "Recovery: index {Index} used (running highest={Highest}, contracts so far={Count})",
                    index, highestUsed, discoveredContracts.Count);
            }
            else
            {
                consecutiveMisses++;
                if (consecutiveMisses >= options.GapLimit)
                {
                    logger?.LogInformation(
                        "Recovery: hit gap limit of {Gap} after index {Index}; stopping scan",
                        options.GapLimit, index);
                    break;
                }
            }
        }

        // Persist discovered contracts. We do this after the scan so we can
        // dedupe by script (one provider may reconstruct a contract another
        // provider already touched, e.g. boltz reconstructing a VHTLC whose
        // settled VTXO the indexer also reported).
        await PersistDiscoveriesAsync(wallet, discoveredContracts, serverInfo.SignerKey, cancellationToken);

        if (highestUsed >= 0)
        {
            var newLastUsed = Math.Max(highestUsed + 1, wallet.LastUsedIndex);
            if (newLastUsed != wallet.LastUsedIndex)
            {
                logger?.LogInformation(
                    "Recovery: bumping wallet {WalletId} LastUsedIndex {Old} -> {New}",
                    walletId, wallet.LastUsedIndex, newLastUsed);
                await walletStorage.UpdateLastUsedIndex(walletId, newLastUsed, cancellationToken);
            }
        }

        var report = new RecoveryReport(
            highestUsed,
            scanned,
            discoveredContracts,
            providerHits);
        logger?.LogInformation(
            "Recovery scan finished for wallet {WalletId}: scanned={Scanned}, highestUsed={Highest}, discovered={Count}",
            walletId, scanned, highestUsed, discoveredContracts.Count);
        return report;
    }

    private static OutputDescriptor DeriveAtIndex(string accountDescriptor, int index, Network network)
    {
        // Mirror HierarchicalDeterministicAddressProvider.GetDescriptorFromIndex:
        // the AccountDescriptor stores the wildcard form `tr([origin]xpub/*)`,
        // and the concrete descriptor at index N is the wildcard with `/*`
        // replaced by `/N`. We also tolerate /0/* legacy form by replacing /0/* first.
        var resolved = accountDescriptor.Contains("/0/*")
            ? accountDescriptor.Replace("/0/*", $"/0/{index}")
            : accountDescriptor.Replace("/*", $"/{index}");
        return OutputDescriptor.Parse(resolved, network);
    }

    private async Task PersistDiscoveriesAsync(
        ArkWalletInfo wallet,
        IReadOnlyList<DiscoveredContract> discovered,
        OutputDescriptor serverKey,
        CancellationToken cancellationToken)
    {
        if (discovered.Count == 0) return;

        // Dedupe by script — different providers may reconstruct the same
        // contract; persist whichever we saw first.
        var seenScripts = new HashSet<string>();
        foreach (var entry in discovered)
        {
            var script = entry.Contract.GetScriptPubKey().ToHex();
            if (!seenScripts.Add(script)) continue;

            var entity = entry.Contract.ToEntity(
                wallet.Id,
                serverKey,
                activityState: ContractActivityState.Active) with
            {
                Metadata = new Dictionary<string, string>
                {
                    ["Source"] = $"recovery:{entry.ProviderName}",
                    ["RecoveryIndex"] = entry.Index.ToString(),
                },
            };
            try
            {
                await contractStorage.SaveContract(entity, cancellationToken);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex,
                    "Recovery: failed to persist contract at index {Index} from {Provider}",
                    entry.Index, entry.ProviderName);
            }
        }
    }
}
