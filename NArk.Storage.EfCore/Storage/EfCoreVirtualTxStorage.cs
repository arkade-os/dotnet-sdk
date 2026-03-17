using Microsoft.EntityFrameworkCore;
using NArk.Abstractions.VirtualTxs;
using NArk.Storage.EfCore.Entities;
using NBitcoin;

namespace NArk.Storage.EfCore.Storage;

public class EfCoreVirtualTxStorage(IArkDbContextFactory contextFactory) : IVirtualTxStorage
{
    public async Task UpsertVirtualTxsAsync(IReadOnlyList<VirtualTx> txs, CancellationToken cancellationToken = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(cancellationToken);
        var virtualTxs = ctx.Set<VirtualTxEntity>();

        // Batch lookup to avoid N+1 queries
        var txids = txs.Select(t => t.Txid).ToList();
        var existingEntities = await virtualTxs
            .Where(e => txids.Contains(e.Txid))
            .ToDictionaryAsync(e => e.Txid, cancellationToken);

        foreach (var tx in txs)
        {
            if (existingEntities.TryGetValue(tx.Txid, out var existing))
            {
                if (tx.Hex is not null)
                    existing.Hex = tx.Hex;
                if (tx.ExpiresAt is not null)
                    existing.ExpiresAt = tx.ExpiresAt;
            }
            else
            {
                virtualTxs.Add(new VirtualTxEntity
                {
                    Txid = tx.Txid,
                    Hex = tx.Hex,
                    ExpiresAt = tx.ExpiresAt
                });
            }
        }

        await ctx.SaveChangesAsync(cancellationToken);
    }

    public async Task SetBranchAsync(OutPoint vtxoOutpoint, IReadOnlyList<VtxoBranch> branch, CancellationToken cancellationToken = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(cancellationToken);
        var branches = ctx.Set<VtxoBranchEntity>();

        // Remove existing branch entries for this VTXO
        var existing = await branches
            .Where(b => b.VtxoTxid == vtxoOutpoint.Hash.ToString() && b.VtxoVout == (int)vtxoOutpoint.N)
            .ToListAsync(cancellationToken);
        branches.RemoveRange(existing);

        // Add new branch entries
        foreach (var entry in branch)
        {
            branches.Add(new VtxoBranchEntity
            {
                VtxoTxid = entry.VtxoTxid,
                VtxoVout = (int)entry.VtxoVout,
                VirtualTxid = entry.VirtualTxid,
                Position = entry.Position
            });
        }

        await ctx.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<VirtualTx>> GetBranchAsync(OutPoint vtxoOutpoint, CancellationToken cancellationToken = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(cancellationToken);
        var txid = vtxoOutpoint.Hash.ToString();
        var vout = (int)vtxoOutpoint.N;

        return await ctx.Set<VtxoBranchEntity>()
            .Where(b => b.VtxoTxid == txid && b.VtxoVout == vout)
            .OrderBy(b => b.Position)
            .Join(ctx.Set<VirtualTxEntity>(),
                b => b.VirtualTxid,
                v => v.Txid,
                (b, v) => new VirtualTx(v.Txid, v.Hex, v.ExpiresAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<VirtualTx?> GetVirtualTxAsync(string txid, CancellationToken cancellationToken = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await ctx.Set<VirtualTxEntity>().FindAsync([txid], cancellationToken);
        return entity is null ? null : new VirtualTx(entity.Txid, entity.Hex, entity.ExpiresAt);
    }

    public async Task PruneForSpentVtxoAsync(OutPoint vtxoOutpoint, CancellationToken cancellationToken = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(cancellationToken);
        var txid = vtxoOutpoint.Hash.ToString();
        var vout = (int)vtxoOutpoint.N;

        // Use a serializable transaction to prevent concurrent prune races:
        // two sibling VTXOs pruning simultaneously could both see the other's
        // references and skip orphan cleanup, or both delete the same tx.
        await using var dbTx = await ctx.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, cancellationToken);

        // Get the virtual txids referenced by this VTXO's branch
        var branchTxids = await ctx.Set<VtxoBranchEntity>()
            .Where(b => b.VtxoTxid == txid && b.VtxoVout == vout)
            .Select(b => b.VirtualTxid)
            .ToListAsync(cancellationToken);

        // Remove the branch entries
        var branchEntries = await ctx.Set<VtxoBranchEntity>()
            .Where(b => b.VtxoTxid == txid && b.VtxoVout == vout)
            .ToListAsync(cancellationToken);
        ctx.Set<VtxoBranchEntity>().RemoveRange(branchEntries);

        // Find orphaned virtual txs (no remaining branch references)
        foreach (var virtualTxid in branchTxids)
        {
            var hasOtherRefs = await ctx.Set<VtxoBranchEntity>()
                .AnyAsync(b => b.VirtualTxid == virtualTxid
                    && !(b.VtxoTxid == txid && b.VtxoVout == vout), cancellationToken);

            if (!hasOtherRefs)
            {
                var virtualTx = await ctx.Set<VirtualTxEntity>().FindAsync([virtualTxid], cancellationToken);
                if (virtualTx is not null)
                    ctx.Set<VirtualTxEntity>().Remove(virtualTx);
            }
        }

        await ctx.SaveChangesAsync(cancellationToken);
        await dbTx.CommitAsync(cancellationToken);
    }

    public async Task<bool> HasBranchAsync(OutPoint vtxoOutpoint, CancellationToken cancellationToken = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(cancellationToken);
        var txid = vtxoOutpoint.Hash.ToString();
        var vout = (int)vtxoOutpoint.N;

        return await ctx.Set<VtxoBranchEntity>()
            .AnyAsync(b => b.VtxoTxid == txid && b.VtxoVout == vout, cancellationToken);
    }
}
