using NArk.Abstractions.Recovery;
using NArk.Abstractions.Wallets;
using NArk.Swaps.Models;

namespace NArk.Swaps.Recovery;

/// <summary>
/// Outcome of a unified <see cref="IWalletRecoveryService.RecoverAsync"/> run.
/// </summary>
/// <param name="WalletType">The recovered wallet's type (HD vs SingleKey).</param>
/// <param name="HdScan">
/// The HD index-scan report (contracts + highest used index), or <c>null</c> for
/// a SingleKey wallet (whose contract set is fixed by its single key — no scan).
/// </param>
/// <param name="ContractsRecovered">Total contracts persisted for the wallet after recovery.</param>
/// <param name="RestoredSwaps">
/// Swaps restored directly during this run. For HD wallets boltz swaps are
/// restored inside the index scan (via the boltz discovery provider) and surface
/// in <see cref="SwapAudit"/> rather than here; this list carries the SingleKey
/// direct-restore results.
/// </param>
/// <param name="SwapAudit">
/// Recoverability snapshot of every known swap for the wallet (settled / refunded
/// / recoverable / …) — the post-recovery swap state.
/// </param>
/// <param name="FinalizedPendingTxIds">Ark tx ids of in-flight transactions finalized during recovery.</param>
/// <param name="FundsScriptsSynced">Number of VTXOs synced from the indexer for the recovered offchain scripts.</param>
public record WalletRecoveryReport(
    WalletType WalletType,
    RecoveryReport? HdScan,
    int ContractsRecovered,
    IReadOnlyList<ArkSwap> RestoredSwaps,
    IReadOnlyList<SwapRecoveryInfo> SwapAudit,
    IReadOnlyList<string> FinalizedPendingTxIds,
    int FundsScriptsSynced);
