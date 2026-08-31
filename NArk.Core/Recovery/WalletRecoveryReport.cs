using NArk.Abstractions.Recovery;
using NArk.Abstractions.Wallets;

namespace NArk.Core.Recovery;

/// <summary>
/// Outcome of a unified <see cref="IWalletRecoveryService.RecoverAsync"/> run.
/// </summary>
/// <param name="WalletType">The recovered wallet's type (HD vs SingleKey).</param>
/// <param name="HdScan">
/// The HD index-scan report (contracts + highest used index), or <c>null</c> for
/// a SingleKey wallet (whose contract set is fixed by its single key — no scan).
/// </param>
/// <param name="ContractsRecovered">Contracts newly discovered + persisted by this run (delta, not the total in storage).</param>
/// <param name="FinalizedPendingTxIds">Arkade tx ids of in-flight transactions finalized during recovery.</param>
/// <param name="FundsScriptsSynced">Number of VTXOs synced from the indexer for the recovered offchain scripts.</param>
public record WalletRecoveryReport(
    WalletType WalletType,
    RecoveryReport? HdScan,
    int ContractsRecovered,
    IReadOnlyList<string> FinalizedPendingTxIds,
    int FundsScriptsSynced);
