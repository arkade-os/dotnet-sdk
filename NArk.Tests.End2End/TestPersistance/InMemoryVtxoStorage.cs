using System.Collections.Concurrent;
using NArk.Abstractions.VTXOs;

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

    public Task<IReadOnlyCollection<ArkVtxo>> GetVtxos(VtxoFilter filter, CancellationToken cancellationToken = default)
    {
        IEnumerable<ArkVtxo> query = _vtxos.Values;

        // Filter by scripts
        if (filter.Scripts is { Count: > 0 })
        {
            var scriptSet = filter.Scripts.ToHashSet();
            query = query.Where(v => scriptSet.Contains(v.Script));
        }

        // Filter by outpoints
        if (filter.Outpoints is { Count: > 0 })
        {
            var outpointSet = filter.Outpoints.Select(op => op.ToString()).ToHashSet();
            query = query.Where(v => outpointSet.Contains(v.OutPoint.ToString()));
        }

        // Note: WalletIds filter not supported in in-memory implementation (no wallet association tracking)

        // Filter by spent state
        if (!filter.IncludeSpent)
        {
            query = query.Where(v => !v.IsSpent());
        }

        // Filter by recoverable state
        if (!filter.IncludeRecoverable)
        {
            query = query.Where(v => !v.IsRecoverable());
        }

        // Search text filter
        if (!string.IsNullOrEmpty(filter.SearchText))
        {
            query = query.Where(v =>
                v.TransactionId.Contains(filter.SearchText, StringComparison.OrdinalIgnoreCase) ||
                v.Script.Contains(filter.SearchText, StringComparison.OrdinalIgnoreCase));
        }

        // Pagination
        if (filter.Skip.HasValue)
        {
            query = query.Skip(filter.Skip.Value);
        }

        if (filter.Take.HasValue)
        {
            query = query.Take(filter.Take.Value);
        }

        return Task.FromResult<IReadOnlyCollection<ArkVtxo>>(query.ToList());
    }
}