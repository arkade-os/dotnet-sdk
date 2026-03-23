using Microsoft.Extensions.Logging;
using NArk.Abstractions.VirtualTxs;
using NArk.Core.Transport;
using NArk.Core.Transport.Models;
using NBitcoin;

namespace NArk.Core.Services;

/// <summary>
/// Fetches, stores, and prunes virtual transaction data for VTXOs.
/// Virtual txs form the tree of pre-signed transactions from commitment tx to VTXO leaf.
/// This data is required for unilateral exit (broadcasting the chain to claim funds on-chain).
/// </summary>
public class VirtualTxService(
    IClientTransport transport,
    IVirtualTxStorage storage,
    ILogger<VirtualTxService>? logger = null)
{
    /// <summary>
    /// Fetch and store the virtual tx branch for a VTXO.
    /// In Lite mode, stores only txids and expiry. In Full mode, also fetches and stores raw tx hex.
    /// </summary>
    public async Task FetchAndStoreBranchAsync(
        OutPoint vtxoOutpoint,
        VirtualTxMode mode = VirtualTxMode.Full,
        CancellationToken cancellationToken = default)
    {
        // Skip if we already have a branch for this VTXO
        if (await storage.HasBranchAsync(vtxoOutpoint, cancellationToken))
        {
            logger?.LogDebug("Branch already exists for VTXO {Outpoint}, skipping fetch", vtxoOutpoint);
            return;
        }

        logger?.LogDebug("Fetching virtual tx chain for VTXO {Outpoint} (mode={Mode})", vtxoOutpoint, mode);

        // 1. Get the chain from arkd indexer (commitment → leaf order)
        var chainEntries = await transport.GetVtxoChainAsync(vtxoOutpoint, cancellationToken);

        if (chainEntries.Count == 0)
        {
            logger?.LogWarning("Empty chain returned for VTXO {Outpoint}", vtxoOutpoint);
            return;
        }

        // 2. Filter to only virtual tx types (skip Commitment — already on-chain)
        var virtualEntries = chainEntries
            .Where(e => e.Type is ChainedTxType.Tree or ChainedTxType.Ark or ChainedTxType.Checkpoint)
            .ToList();

        if (virtualEntries.Count == 0)
        {
            logger?.LogDebug("No virtual txs in chain for VTXO {Outpoint} (all on-chain)", vtxoOutpoint);
            return;
        }

        // 3. Create VirtualTx records (txid + expiry, hex is null in Lite mode)
        var virtualTxs = virtualEntries
            .Select(e => new VirtualTx(e.Txid, null, e.ExpiresAt))
            .ToList();

        // 4. In Full mode, fetch raw tx hex
        if (mode == VirtualTxMode.Full)
        {
            var txids = virtualEntries.Select(e => e.Txid).ToList();
            var hexList = await transport.GetVirtualTxsAsync(txids, cancellationToken);

            // Map hex back to virtual txs by index
            if (hexList.Count == txids.Count)
            {
                for (var i = 0; i < virtualTxs.Count; i++)
                {
                    virtualTxs[i] = virtualTxs[i] with { Hex = hexList[i] };
                }
            }
            else
            {
                logger?.LogWarning(
                    "Virtual tx hex count mismatch for VTXO {Outpoint}: expected {Expected}, got {Actual}",
                    vtxoOutpoint, txids.Count, hexList.Count);
            }
        }

        // 5. Upsert VirtualTx records (shared across sibling VTXOs)
        await storage.UpsertVirtualTxsAsync(virtualTxs, cancellationToken);

        // 6. Create branch entries linking this VTXO to its chain
        var branches = virtualEntries
            .Select((e, i) => new VtxoBranch(
                vtxoOutpoint.Hash.ToString(),
                vtxoOutpoint.N,
                e.Txid,
                i))
            .ToList();

        await storage.SetBranchAsync(vtxoOutpoint, branches, cancellationToken);

        logger?.LogInformation(
            "Stored {Count} virtual txs for VTXO {Outpoint} (mode={Mode})",
            virtualTxs.Count, vtxoOutpoint, mode);
    }

    /// <summary>
    /// Ensure all virtual txs in a VTXO's branch have hex populated.
    /// Upgrades Lite → Full by fetching missing hex on demand.
    /// </summary>
    public async Task EnsureHexPopulatedAsync(
        OutPoint vtxoOutpoint,
        CancellationToken cancellationToken = default)
    {
        var branch = await storage.GetBranchAsync(vtxoOutpoint, cancellationToken);
        if (branch.Count == 0)
        {
            // No branch stored — fetch everything in Full mode
            await FetchAndStoreBranchAsync(vtxoOutpoint, VirtualTxMode.Full, cancellationToken);
            return;
        }

        // Find txs missing hex
        var missingHex = branch.Where(tx => tx.Hex is null).ToList();
        if (missingHex.Count == 0)
        {
            logger?.LogDebug("All virtual txs already have hex for VTXO {Outpoint}", vtxoOutpoint);
            return;
        }

        logger?.LogDebug("Fetching hex for {Count} virtual txs for VTXO {Outpoint}",
            missingHex.Count, vtxoOutpoint);

        var txids = missingHex.Select(tx => tx.Txid).ToList();
        var hexList = await transport.GetVirtualTxsAsync(txids, cancellationToken);

        if (hexList.Count != txids.Count)
        {
            logger?.LogWarning(
                "Hex count mismatch when populating VTXO {Outpoint}: expected {Expected}, got {Actual}",
                vtxoOutpoint, txids.Count, hexList.Count);
        }

        // Update existing records with hex
        var updates = new List<VirtualTx>();
        for (var i = 0; i < Math.Min(txids.Count, hexList.Count); i++)
        {
            updates.Add(new VirtualTx(txids[i], hexList[i], missingHex[i].ExpiresAt));
        }

        await storage.UpsertVirtualTxsAsync(updates, cancellationToken);

        logger?.LogInformation("Populated hex for {Count} virtual txs for VTXO {Outpoint}",
            updates.Count, vtxoOutpoint);
    }

    /// <summary>
    /// Prune virtual tx data for spent VTXOs.
    /// Removes branch entries, then cleans up orphaned VirtualTx rows.
    /// </summary>
    public async Task PruneForSpentVtxosAsync(
        IReadOnlyCollection<OutPoint> spentOutpoints,
        CancellationToken cancellationToken = default)
    {
        foreach (var outpoint in spentOutpoints)
        {
            await storage.PruneForSpentVtxoAsync(outpoint, cancellationToken);
        }

        if (spentOutpoints.Count > 0)
        {
            logger?.LogDebug("Pruned virtual tx data for {Count} spent VTXOs", spentOutpoints.Count);
        }
    }
}
