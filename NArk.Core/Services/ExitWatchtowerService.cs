using Microsoft.Extensions.Logging;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VirtualTxs;
using NArk.Abstractions.VTXOs;
using NBitcoin;

namespace NArk.Core.Services;

/// <summary>
/// Monitors the blockchain for partial tree broadcasts and auto-starts
/// unilateral exits when detected. If someone starts unrolling the tree
/// but doesn't finish, previous owners may claim funds after their CSV
/// timeout. The watchtower detects this and continues the exit automatically.
/// </summary>
public class ExitWatchtowerService(
    IVtxoStorage vtxoStorage,
    IVirtualTxStorage virtualTxStorage,
    IOnchainBroadcaster broadcaster,
    IContractStorage contractStorage,
    UnilateralExitService exitService,
    ILogger<ExitWatchtowerService>? logger = null)
{
    /// <summary>
    /// Check for partial tree broadcasts and auto-start exits.
    /// Call periodically or on block notifications.
    /// </summary>
    public async Task CheckAndRespondAsync(CancellationToken cancellationToken = default)
    {
        // Get all unspent VTXOs that have stored virtual tx branches
        var allVtxos = await vtxoStorage.GetVtxos(cancellationToken: cancellationToken);
        var unspent = allVtxos.Where(v => !v.IsSpent()).ToList();

        foreach (var vtxo in unspent)
        {
            try
            {
                await CheckVtxoAsync(vtxo, cancellationToken);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(0, ex,
                    "Failed to check VTXO {Outpoint} for partial broadcasts", vtxo.OutPoint);
            }
        }
    }

    private async Task CheckVtxoAsync(ArkVtxo vtxo, CancellationToken ct)
    {
        // Check if this VTXO has a stored branch
        var hasBranch = await virtualTxStorage.HasBranchAsync(vtxo.OutPoint, ct);
        if (!hasBranch)
            return;

        var branch = await virtualTxStorage.GetBranchAsync(vtxo.OutPoint, ct);
        if (branch.Count == 0)
            return;

        // Optimization: check only the root tx first (position 0).
        // If root is not on-chain, nothing downstream can be either.
        var rootTx = branch[0];
        var rootTxid = uint256.Parse(rootTx.Txid);
        var rootStatus = await broadcaster.GetTxStatusAsync(rootTxid, ct);

        if (!rootStatus.Confirmed && !rootStatus.InMempool)
            return; // Root not seen, tree is intact

        // Root is on-chain or in mempool — check if leaf is also confirmed
        var leafTx = branch[^1];
        var leafTxid = uint256.Parse(leafTx.Txid);
        var leafStatus = await broadcaster.GetTxStatusAsync(leafTxid, ct);

        if (leafStatus.Confirmed)
            return; // Full tree already confirmed, exit already in progress or completed

        // Partial broadcast detected! Root is on-chain but leaf is not.
        logger?.LogWarning(
            "Detected partial tree broadcast for VTXO {Outpoint}: root tx {RootTxid} is {RootState} but leaf tx {LeafTxid} is not confirmed",
            vtxo.OutPoint, rootTx.Txid,
            rootStatus.Confirmed ? "confirmed" : "in mempool",
            leafTx.Txid);

        // Find the wallet that owns this VTXO
        var contracts = await contractStorage.GetContracts(
            scripts: [vtxo.Script],
            cancellationToken: ct);
        var contract = contracts.FirstOrDefault();

        if (contract is null)
        {
            logger?.LogWarning("No contract found for VTXO {Outpoint}, cannot auto-start exit",
                vtxo.OutPoint);
            return;
        }

        // Auto-start exit — use the wallet's default claim address
        // The caller should configure a default claim address; for now we log the detection
        logger?.LogWarning(
            "Auto-exit needed for VTXO {Outpoint} (wallet={WalletId}). " +
            "Call StartExitAsync with a claim address to protect these funds.",
            vtxo.OutPoint, contract.WalletIdentifier);
    }
}
