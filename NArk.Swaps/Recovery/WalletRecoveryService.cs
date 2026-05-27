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
    IContractService contractService,
    IContractStorage contractStorage,
    HdWalletRecoveryService hdRecovery,
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
            // SingleKey: the contract set is fixed by the single key. Ensure that
            // contract exists, then restore swaps for its descriptor directly
            // (there is no index to scan).
            await EnsureSingleKeyContractAsync(wallet, cancellationToken);
            if (!string.IsNullOrEmpty(wallet.AccountDescriptor))
            {
                var network = (await clientTransport.GetServerInfoAsync(cancellationToken)).Network;
                var descriptor = KeyExtensions.ParseOutputDescriptor(wallet.AccountDescriptor!, network);
                restoredSwaps.AddRange(await swaps.RestoreSwaps(walletId, [descriptor], cancellationToken));
            }
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

        logger?.LogInformation(
            "Recovered wallet {WalletId}: {Contracts} contracts, {Swaps} swaps audited, {Pending} pending finalized, {Vtxos} VTXOs synced",
            walletId, contracts.Count, swapAudit.Count, finalized.Count, vtxosSynced);

        return new WalletRecoveryReport(
            wallet.WalletType,
            hdScan,
            contracts.Count,
            restoredSwaps,
            swapAudit,
            finalized,
            vtxosSynced);
    }

    /// <summary>
    /// Derives the SingleKey wallet's deterministic default contract if storage
    /// holds none (e.g. fresh import into cleared storage). Idempotent — does
    /// nothing when a contract already exists.
    /// </summary>
    private async Task EnsureSingleKeyContractAsync(ArkWalletInfo wallet, CancellationToken cancellationToken)
    {
        var existing = await contractStorage.GetContracts(
            walletIds: [wallet.Id], cancellationToken: cancellationToken);
        if (existing.Count > 0)
            return;

        await contractService.DeriveContract(
            wallet.Id,
            NextContractPurpose.SendToSelf,
            ContractActivityState.Active,
            metadata: new Dictionary<string, string> { ["Source"] = "recovery" },
            cancellationToken: cancellationToken);
    }
}
