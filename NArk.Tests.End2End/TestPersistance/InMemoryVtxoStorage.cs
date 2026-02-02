using System.Collections.Concurrent;
using NArk.Abstractions.VTXOs;
using NBitcoin;

namespace NArk.Tests.End2End.TestPersistance;

public class InMemoryVtxoStorage : IVtxoStorage
{
    private ConcurrentDictionary<string, ArkVtxo> _vtxos = new();

    public event EventHandler<ArkVtxo>? VtxosChanged;
    public event EventHandler? ActiveScriptsChanged;

    public virtual Task<bool> UpsertVtxo(ArkVtxo vtxo, CancellationToken cancellationToken = default)
    {
        try
        {
            _vtxos.TryGetValue(vtxo.OutPoint.ToString(), out var oldVtxo);
            _vtxos[vtxo.OutPoint.ToString()] = vtxo;
            return Task.FromResult(oldVtxo is null || vtxo != oldVtxo);
        }
        finally
        {
            VtxosChanged?.Invoke(this, vtxo);
            ActiveScriptsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public Task<IReadOnlyCollection<ArkVtxo>> GetVtxos(
        IReadOnlyCollection<string>? scripts = null,
        IReadOnlyCollection<OutPoint>? outpoints = null,
        string[]? walletIds = null,
        bool includeSpent = false,
        string? searchText = null,
        int? skip = null,
        int? take = null,
        CancellationToken cancellationToken = default)
    {
        IEnumerable<ArkVtxo> query = _vtxos.Values;

        // Filter by scripts
        if (scripts is { Count: > 0 })
        {
            var scriptSet = scripts.ToHashSet();
            query = query.Where(v => scriptSet.Contains(v.Script));
        }

        // Filter by outpoints
        if (outpoints is { Count: > 0 })
        {
            var outpointSet = outpoints.Select(op => op.ToString()).ToHashSet();
            query = query.Where(v => outpointSet.Contains(v.OutPoint.ToString()));
        }

        // Note: WalletIds filter not supported in in-memory implementation (no wallet association tracking)

        // Filter by spent state
        if (!includeSpent)
        {
            query = query.Where(v => !v.IsSpent());
        }

        // Search text filter
        if (!string.IsNullOrEmpty(searchText))
        {
            query = query.Where(v =>
                v.TransactionId.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                v.Script.Contains(searchText, StringComparison.OrdinalIgnoreCase));
        }

        // Pagination
        if (skip.HasValue)
        {
            query = query.Skip(skip.Value);
        }

        if (take.HasValue)
        {
            query = query.Take(take.Value);
        }

        return Task.FromResult<IReadOnlyCollection<ArkVtxo>>(query.ToList());
    }
}