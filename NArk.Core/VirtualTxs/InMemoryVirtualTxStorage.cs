using System.Collections.Concurrent;
using NArk.Abstractions.VirtualTxs;
using NBitcoin;

namespace NArk.Core.VirtualTxs;

/// <summary>
/// In-process <see cref="IVirtualTxStorage"/> with no durable backing store.
/// State lives for the lifetime of the host — perfect for emergency-exit
/// tooling, plugins, or ephemeral wallets that don't want the EF Core schema
/// cost. If the host restarts mid-exit, the cached chain is lost and the
/// next <c>EnsureHexPopulatedAsync</c> call re-fetches from the indexer.
/// </summary>
/// <remarks>
/// Within a process this storage is fully compatible with the existing
/// <c>VirtualTxService</c> / <c>UnilateralExitService</c> flow — same code
/// paths, just no SQL.
/// </remarks>
public class InMemoryVirtualTxStorage : IVirtualTxStorage
{
    /// <summary>txid → VirtualTx record (txid + hex + expiry + type).</summary>
    private readonly ConcurrentDictionary<string, VirtualTx> _virtualTxs = new();

    /// <summary>(vtxoTxid, vtxoVout) → ordered list of VtxoBranch entries.</summary>
    private readonly ConcurrentDictionary<(string Txid, uint Vout), List<VtxoBranch>> _branches = new();

    public Task UpsertVirtualTxsAsync(IReadOnlyList<VirtualTx> txs, CancellationToken cancellationToken = default)
    {
        foreach (var tx in txs)
        {
            _virtualTxs.AddOrUpdate(
                tx.Txid,
                tx,
                // Match EF semantics: update non-null fields only so a later
                // Lite refresh doesn't blow away hex stored by a prior Full
                // fetch for the same txid.
                (_, existing) => existing with
                {
                    Hex = tx.Hex ?? existing.Hex,
                    ExpiresAt = tx.ExpiresAt ?? existing.ExpiresAt,
                    Type = tx.Type != ChainedTxType.Unspecified ? tx.Type : existing.Type,
                });
        }
        return Task.CompletedTask;
    }

    public Task SetBranchAsync(OutPoint vtxoOutpoint, IReadOnlyList<VtxoBranch> branch, CancellationToken cancellationToken = default)
    {
        _branches[(vtxoOutpoint.Hash.ToString(), vtxoOutpoint.N)] = branch.OrderBy(b => b.Position).ToList();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VirtualTx>> GetBranchAsync(OutPoint vtxoOutpoint, CancellationToken cancellationToken = default)
    {
        if (!_branches.TryGetValue((vtxoOutpoint.Hash.ToString(), vtxoOutpoint.N), out var entries))
            return Task.FromResult<IReadOnlyList<VirtualTx>>([]);

        IReadOnlyList<VirtualTx> result = entries
            .OrderBy(b => b.Position)
            .Select(b => _virtualTxs.TryGetValue(b.VirtualTxid, out var v)
                ? v
                : new VirtualTx(b.VirtualTxid, null, null))
            .ToList();
        return Task.FromResult(result);
    }

    public Task<VirtualTx?> GetVirtualTxAsync(string txid, CancellationToken cancellationToken = default)
        => Task.FromResult(_virtualTxs.TryGetValue(txid, out var v) ? v : null);

    public Task PruneForSpentVtxoAsync(OutPoint vtxoOutpoint, CancellationToken cancellationToken = default)
    {
        if (!_branches.TryRemove((vtxoOutpoint.Hash.ToString(), vtxoOutpoint.N), out var removed))
            return Task.CompletedTask;

        // Orphan cleanup: drop VirtualTx rows no longer referenced by any
        // remaining branch. Matches EF impl's "ref count = 0 → delete".
        var stillReferenced = _branches.Values
            .SelectMany(b => b)
            .Select(b => b.VirtualTxid)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var entry in removed)
        {
            if (!stillReferenced.Contains(entry.VirtualTxid))
                _virtualTxs.TryRemove(entry.VirtualTxid, out _);
        }

        return Task.CompletedTask;
    }

    public Task<bool> HasBranchAsync(OutPoint vtxoOutpoint, CancellationToken cancellationToken = default)
    {
        var has = _branches.TryGetValue((vtxoOutpoint.Hash.ToString(), vtxoOutpoint.N), out var entries)
                  && entries.Count > 0;
        return Task.FromResult(has);
    }
}
