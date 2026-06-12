using Microsoft.Extensions.Logging;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Recovery;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Recovery;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Swaps.Models;
using NArk.Swaps.Services;

namespace NArk.Swaps.Recovery;

/// <summary>
/// Unified, wallet-type-agnostic recovery. Composes the existing building blocks
/// — the HD index scanner (<see cref="HdWalletRecoveryService"/>), the pending-tx
/// finalizer (<see cref="PendingArkTransactionRecoveryService"/>), boltz swap
/// restore/audit (<see cref="SwapsManagementService"/>), and the VTXO sync
/// (<see cref="VtxoSynchronizationService"/>) — behind one <see cref="RecoverAsync"/>
/// call. Lives in NArk.Swaps because it needs both the Core recovery services and
/// the swap services (Swaps depends on Core, not the reverse).
/// </summary>
public class WalletRecoveryService(
    IWalletStorage walletStorage,
    IContractStorage contractStorage,
    HdWalletRecoveryService hdRecovery,
    SingleKeyVtxoRecoveryService singleKeyRecovery,
    PendingArkTransactionRecoveryService pendingTxRecovery,
    SwapsManagementService swaps,
    VtxoSynchronizationService vtxoSync,
    IClientTransport clientTransport,
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
        var restoredSwaps = new List<ArkSwap>();

        if (wallet.WalletType == WalletType.HD)
        {
            // The HD index scan discovers contracts across derivation indices and
            // server signers (incl. deprecated/legacy), and restores boltz swaps
            // in-line via the boltz discovery provider.
            hdScan = await hdRecovery.ScanAsync(walletId, options, cancellationToken);
        }
        else
        {
            // SingleKey: the contract set is fixed by the single key. Probe deprecated
            // signers once (no index to scan), then ensure the current-signer default
            // exists (idempotent; mints the new default after rotation). Finally restore
            // swaps for its descriptor directly.
            if (string.IsNullOrEmpty(wallet.AccountDescriptor))
                throw new InvalidOperationException(
                    $"SingleKey wallet '{walletId}' has no AccountDescriptor; cannot recover.");

            await singleKeyRecovery.DiscoverAsync(walletId, cancellationToken);
            await singleKeyRecovery.EnsureDefaultAsync(walletId, cancellationToken);
            var network = (await clientTransport.GetServerInfoAsync(cancellationToken)).Network;
            var descriptor = KeyExtensions.ParseOutputDescriptor(wallet.AccountDescriptor!, network);
            restoredSwaps.AddRange(await swaps.RestoreSwaps(walletId, [descriptor], cancellationToken));
        }

        // Finalize any in-flight Ark transactions that were mid-submit.
        var finalized = await pendingTxRecovery.FinalizePendingArkTransactionsAsync(walletId, cancellationToken);

        // Sync funds for every recovered offchain contract so balances repopulate
        // deterministically (boarding UTXOs are reconciled by their own on-chain
        // discovery/sync path, not this indexer poll).
        var contracts = await contractStorage.GetContracts(
            walletIds: [walletId], cancellationToken: cancellationToken);
        var offchainScripts = contracts
            .Where(c => c.Type != ArkBoardingContract.ContractType)
            .Select(c => c.Script)
            .ToHashSet();
        var vtxosSynced = offchainScripts.Count > 0
            ? await vtxoSync.PollScriptsForVtxos(offchainScripts, cancellationToken)
            : 0;

        // Audit the post-recovery state of every known swap for the report.
        var swapAudit = await swaps.ScanRecoverableSwapsAsync(walletId, cancellationToken);

        // Contracts NEWLY recovered by this run (not the total in storage).
        var contractsRecovered = Math.Max(0, contracts.Count - contractsBefore);

        logger?.LogInformation(
            "Recovered wallet {WalletId}: {Contracts} new contracts, {Swaps} swaps audited, {Pending} pending finalized, {Vtxos} VTXOs synced",
            walletId, contractsRecovered, swapAudit.Count, finalized.Count, vtxosSynced);

        return new WalletRecoveryReport(
            wallet.WalletType,
            hdScan,
            contractsRecovered,
            restoredSwaps,
            swapAudit,
            finalized,
            vtxosSynced);
    }
}
