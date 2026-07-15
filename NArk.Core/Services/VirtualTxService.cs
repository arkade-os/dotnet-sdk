using Microsoft.Extensions.Logging;
using NArk.Abstractions.VirtualTxs;
using NArk.Core.Transport;
using NArk.Core.VirtualTxs;
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
    IVtxoChainProofProvider proofProvider,
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
        // Skip only once we hold a *broadcast-ready* branch. In Lite mode that
        // is just "a branch exists" (txids only). In Full mode a branch fetched
        // right after a batch can still carry a sig-less template — a tree
        // node's MuSig2 signature only propagates once finalization completes —
        // so re-fetch until every off-chain row is fully signed. Freezing a
        // template here is what forced unilateral exit to re-query the operator
        // later; capturing the signed copy makes exit operator-independent.
        var alreadyHave = mode == VirtualTxMode.Lite
            ? await storage.HasBranchAsync(vtxoOutpoint, cancellationToken)
            : await IsBranchBroadcastReadyAsync(vtxoOutpoint, cancellationToken);
        if (alreadyHave)
        {
            logger?.LogDebug("Branch already {State} for VTXO {Outpoint}, skipping fetch",
                mode == VirtualTxMode.Lite ? "present" : "broadcast-ready", vtxoOutpoint);
            return;
        }

        logger?.LogDebug("Fetching virtual tx chain for VTXO {Outpoint} (mode={Mode})", vtxoOutpoint, mode);

        // 1. Get the chain from arkd indexer (leaf → commitment order — the
        //    VTXO's own tx first, ending at the on-chain Commitment root).
        //    The full chain — including the commitment root — is stored so
        //    consumers can walk back to the anchor without a second indexer
        //    call. Each row carries its ChainedTxType so UnilateralExitService
        //    can skip Commitment when broadcasting.
        //    Present an ownership proof when we can build one so the indexer
        //    serves the chain on withheld/private servers (and returns the
        //    fully-signed virtual txs); otherwise fall back to anonymous lookup.
        var proof = await proofProvider.TryCreateProofAsync(vtxoOutpoint, cancellationToken);
        var chainEntries = await transport.GetVtxoChainAsync(
            vtxoOutpoint, proof?.Proof, proof?.Message, cancellationToken);

        if (chainEntries.Count == 0)
        {
            logger?.LogWarning("Empty chain returned for VTXO {Outpoint}", vtxoOutpoint);
            return;
        }

        // 2. Create VirtualTx records — txid + expiry + type. Hex is null
        //    for Lite mode (and for Commitment txs we never fetch hex for
        //    even in Full mode, since they're already on-chain).
        var virtualTxs = chainEntries
            .Select(e => new VirtualTx(e.Txid, null, e.ExpiresAt, e.Type))
            .ToList();

        // 3. In Full mode, fetch raw tx hex for the off-chain virtual txs
        //    only. Commitment txs stay hex-null since arkd's GetVirtualTxs
        //    is for tree/ark/checkpoint nodes.
        if (mode == VirtualTxMode.Full)
        {
            // Don't refetch a row we already hold broadcast-ready: the send path
            // persists the signed ark/checkpoint we produced, and arkd can serve
            // a stale/sig-less copy for the same txid. Upsert never overwrites
            // with null, so skipping the fetch preserves the stored signed copy.
            var txidsToFetch = new List<string>();
            foreach (var e in chainEntries.Where(e => e.Type is ChainedTxType.Tree
                                                            or ChainedTxType.Ark
                                                            or ChainedTxType.Checkpoint))
            {
                var existing = await storage.GetVirtualTxAsync(e.Txid, cancellationToken);
                if (existing?.Hex is not null && VirtualTxFinalizer.IsBroadcastReady(existing.Hex))
                    continue;
                txidsToFetch.Add(e.Txid);
            }

            if (txidsToFetch.Count > 0)
            {
                var hexList = await transport.GetVirtualTxsAsync(txidsToFetch, cancellationToken);
                if (hexList.Count == txidsToFetch.Count)
                {
                    var hexByTxid = txidsToFetch
                        .Zip(hexList, (id, hex) => (id, hex))
                        .ToDictionary(t => t.id, t => t.hex);
                    for (var i = 0; i < virtualTxs.Count; i++)
                    {
                        if (hexByTxid.TryGetValue(virtualTxs[i].Txid, out var hex))
                            virtualTxs[i] = virtualTxs[i] with { Hex = hex };
                    }
                }
                else
                {
                    logger?.LogWarning(
                        "Virtual tx hex count mismatch for VTXO {Outpoint}: expected {Expected}, got {Actual}",
                        vtxoOutpoint, txidsToFetch.Count, hexList.Count);
                }
            }
        }

        // 4. Upsert VirtualTx records (shared across sibling VTXOs)
        await storage.UpsertVirtualTxsAsync(virtualTxs, cancellationToken);

        // 5. Create branch entries linking this VTXO to its chain
        var branches = chainEntries
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

        // In Full mode, surface a still-unsigned branch so the caller (auto-fetch)
        // knows to retry: arkd can serve a tree row before its MuSig2 signature
        // has propagated, and we must capture the signed copy — not freeze the
        // template — for exit to work without the operator later.
        if (mode == VirtualTxMode.Full
            && !await IsBranchBroadcastReadyAsync(vtxoOutpoint, cancellationToken))
        {
            logger?.LogInformation(
                "Virtual tx branch for VTXO {Outpoint} is not yet broadcast-ready " +
                "(operator likely hasn't finalized signatures); will refetch on next attempt",
                vtxoOutpoint);
        }
    }

    /// <summary>
    /// True when every off-chain row of the VTXO's stored branch is fully signed —
    /// i.e. broadcastable from storage alone, with no operator refetch. Commitment
    /// rows are on-chain anchors and are skipped. Returns false when the branch is
    /// missing, has a null-hex off-chain row, or still holds a sig-less template.
    /// </summary>
    public async Task<bool> IsBranchBroadcastReadyAsync(
        OutPoint vtxoOutpoint, CancellationToken cancellationToken = default)
    {
        var branch = await storage.GetBranchAsync(vtxoOutpoint, cancellationToken);
        if (branch.Count == 0)
            return false;

        foreach (var vtx in branch)
        {
            if (vtx.Type == ChainedTxType.Commitment)
                continue;
            if (vtx.Hex is null || !VirtualTxFinalizer.IsBroadcastReady(vtx.Hex))
                return false;
        }

        return true;
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

        // Find txs missing hex. Commitment txs are on-chain anchors;
        // arkd's GetVirtualTxs doesn't carry hex for them, so skip those
        // when deciding whether the branch is "populated".
        var missingHex = branch
            .Where(tx => tx.Hex is null && tx.Type != ChainedTxType.Commitment)
            .ToList();
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
