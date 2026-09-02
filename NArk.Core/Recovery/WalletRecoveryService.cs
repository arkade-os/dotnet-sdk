using Microsoft.Extensions.Logging;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Recovery;
using NArk.Abstractions.Wallets;
using NArk.Core.Services;

namespace NArk.Core.Recovery;

/// <summary>
/// Unified, wallet-type-agnostic recovery. Composes the existing building blocks
/// — the HD index scanner (<see cref="HdWalletRecoveryService"/>), the pending-tx
/// finalizer (<see cref="PendingArkTransactionRecoveryService"/>) and the VTXO
/// sync (<see cref="VtxoSynchronizationService"/>) — behind one
/// <see cref="RecoverAsync"/> call.
/// </summary>
public class WalletRecoveryService(
    IWalletStorage walletStorage,
    IContractStorage contractStorage,
    HdWalletRecoveryService hdRecovery,
    SingleKeyVtxoRecoveryService singleKeyRecovery,
    PendingArkTransactionRecoveryService pendingTxRecovery,
    VtxoSynchronizationService vtxoSync,
    ILogger<WalletRecoveryService>? logger = null) : IWalletRecoveryService
{
    /// <inheritdoc />
    public async Task<WalletRecoveryReport> RecoverAsync(
        string walletId, RecoveryOptions? options = null, CancellationToken cancellationToken = default)
    {
        var wallet = await walletStorage.GetWalletById(walletId, cancellationToken)
            ?? throw new InvalidOperationException($"Wallet '{walletId}' not found.");

        using var _ = logger?.BeginScope(("RecoverWalletId", walletId));
        logger?.LogInformation("Recovering {WalletType} wallet {WalletId}", wallet.WalletType, walletId);

        // Snapshot the contract count so the report reflects what THIS run recovered
        // (a Rescan on a populated wallet may discover nothing new).
        var contractsBefore = (await contractStorage.GetContracts(
            walletIds: [walletId], cancellationToken: cancellationToken)).Count;

        RecoveryReport? hdScan = null;

        if (wallet.WalletType == WalletType.HD)
        {
            // The HD index scan discovers contracts across derivation indices and
            // server signers (incl. deprecated/legacy).
            hdScan = await hdRecovery.ScanAsync(walletId, options, cancellationToken);
        }
        else
        {
            // SingleKey: the contract set is fixed by the single key. Probe deprecated
            // signers once (no index to scan), then ensure the current-signer default
            // exists (idempotent; mints the new default after rotation).
            if (string.IsNullOrEmpty(wallet.AccountDescriptor))
                throw new InvalidOperationException(
                    $"SingleKey wallet '{walletId}' has no AccountDescriptor; cannot recover.");

            await singleKeyRecovery.DiscoverAsync(walletId, cancellationToken);
            await singleKeyRecovery.EnsureDefaultAsync(walletId, cancellationToken);
        }

        // Finalize any in-flight Arkade transactions that were mid-submit.
        var finalized = await pendingTxRecovery.FinalizePendingArkTransactionsAsync(walletId, cancellationToken);

        // Sync funds for every recovered offchain contract so balances repopulate
        // deterministically (boarding UTXOs are reconciled by their own on-chain
        // discovery/sync path, not this indexer poll).
        var contracts = await contractStorage.GetContracts(
            walletIds: [walletId], cancellationToken: cancellationToken);
        var offchainScripts = contracts
            .Where(c => (c.Scope & ContractScope.Offchain) != 0)
            .Select(c => c.Script)
            .ToHashSet();
        var vtxosSynced = offchainScripts.Count > 0
            ? await vtxoSync.PollScriptsForVtxos(offchainScripts, cancellationToken)
            : 0;

        // Contracts NEWLY recovered by this run (not the total in storage).
        var contractsRecovered = Math.Max(0, contracts.Count - contractsBefore);

        logger?.LogInformation(
            "Recovered wallet {WalletId}: {Contracts} new contracts, {Pending} pending finalized, {Vtxos} VTXOs synced",
            walletId, contractsRecovered, finalized.Count, vtxosSynced);

        return new WalletRecoveryReport(
            wallet.WalletType,
            hdScan,
            contractsRecovered,
            finalized,
            vtxosSynced);
    }
}
