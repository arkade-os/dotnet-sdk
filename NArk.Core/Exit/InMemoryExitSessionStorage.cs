using System.Collections.Concurrent;
using NArk.Abstractions.Exit;
using NBitcoin;

namespace NArk.Core.Exit;

/// <summary>
/// In-process <see cref="IExitSessionStorage"/> with no durable backing store.
/// State lives for the lifetime of the host — perfect for emergency-exit
/// tooling, plugins, or ephemeral wallets that don't want the EF Core schema
/// cost. If the host restarts mid-exit, the session is lost and the consumer
/// has to re-trigger; trade off resumability for zero persistence.
/// </summary>
/// <remarks>
/// Within a process this storage is fully compatible with the existing
/// <c>UnilateralExitService</c> / <c>ExitWatchtowerService</c> flow — same
/// code paths, just no SQL.
/// </remarks>
public class InMemoryExitSessionStorage : IExitSessionStorage
{
    private readonly ConcurrentDictionary<string, ExitSession> _byId = new();
    private readonly ConcurrentDictionary<(string Txid, uint Vout), string> _byVtxo = new();

    public Task UpsertAsync(ExitSession session, CancellationToken cancellationToken = default)
    {
        _byId[session.Id] = session;
        _byVtxo[(session.VtxoTxid, session.VtxoVout)] = session.Id;
        return Task.CompletedTask;
    }

    public Task<ExitSession?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult(_byId.TryGetValue(id, out var s) ? s : null);

    public Task<ExitSession?> GetByVtxoAsync(OutPoint vtxoOutpoint, CancellationToken cancellationToken = default)
    {
        var key = (vtxoOutpoint.Hash.ToString(), vtxoOutpoint.N);
        if (_byVtxo.TryGetValue(key, out var id) && _byId.TryGetValue(id, out var s))
            return Task.FromResult<ExitSession?>(s);
        return Task.FromResult<ExitSession?>(null);
    }

    public Task<IReadOnlyList<ExitSession>> GetByStateAsync(ExitSessionState state, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ExitSession> result = _byId.Values
            .Where(s => s.State == state)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<ExitSession>> GetActiveSessionsAsync(string? walletId = null, CancellationToken cancellationToken = default)
    {
        // "Active" mirrors the EF impl: anything that's not Completed or Failed.
        IReadOnlyList<ExitSession> result = _byId.Values
            .Where(s => s.State != ExitSessionState.Completed && s.State != ExitSessionState.Failed)
            .Where(s => walletId is null || s.WalletId == walletId)
            .ToList();
        return Task.FromResult(result);
    }
}
