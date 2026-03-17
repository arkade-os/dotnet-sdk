using Microsoft.EntityFrameworkCore;
using NArk.Abstractions.Exit;
using NArk.Storage.EfCore.Entities;
using NBitcoin;

namespace NArk.Storage.EfCore.Storage;

public class EfCoreExitSessionStorage(IArkDbContextFactory contextFactory) : IExitSessionStorage
{
    public async Task UpsertAsync(ExitSession session, CancellationToken cancellationToken = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(cancellationToken);
        var sessions = ctx.Set<ExitSessionEntity>();

        var existing = await sessions.FindAsync([session.Id], cancellationToken);
        if (existing is null)
        {
            sessions.Add(new ExitSessionEntity
            {
                Id = session.Id,
                VtxoTxid = session.VtxoTxid,
                VtxoVout = (int)session.VtxoVout,
                WalletId = session.WalletId,
                ClaimAddress = session.ClaimAddress,
                State = session.State,
                NextTxIndex = session.NextTxIndex,
                ClaimTxid = session.ClaimTxid,
                CreatedAt = session.CreatedAt,
                UpdatedAt = session.UpdatedAt,
                FailReason = session.FailReason,
                RetryCount = session.RetryCount
            });
        }
        else
        {
            existing.State = session.State;
            existing.NextTxIndex = session.NextTxIndex;
            existing.ClaimTxid = session.ClaimTxid;
            existing.UpdatedAt = session.UpdatedAt;
            existing.FailReason = session.FailReason;
            existing.RetryCount = session.RetryCount;
        }

        await ctx.SaveChangesAsync(cancellationToken);
    }

    public async Task<ExitSession?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await ctx.Set<ExitSessionEntity>().FindAsync([id], cancellationToken);
        return entity is null ? null : MapToRecord(entity);
    }

    public async Task<ExitSession?> GetByVtxoAsync(OutPoint vtxoOutpoint, CancellationToken cancellationToken = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(cancellationToken);
        var txid = vtxoOutpoint.Hash.ToString();
        var vout = (int)vtxoOutpoint.N;

        var entity = await ctx.Set<ExitSessionEntity>()
            .FirstOrDefaultAsync(e => e.VtxoTxid == txid && e.VtxoVout == vout, cancellationToken);
        return entity is null ? null : MapToRecord(entity);
    }

    public async Task<IReadOnlyList<ExitSession>> GetByStateAsync(ExitSessionState state, CancellationToken cancellationToken = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await ctx.Set<ExitSessionEntity>()
            .Where(e => e.State == state)
            .Select(e => MapToRecord(e))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ExitSession>> GetActiveSessionsAsync(string? walletId = null, CancellationToken cancellationToken = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = ctx.Set<ExitSessionEntity>()
            .Where(e => e.State != ExitSessionState.Completed && e.State != ExitSessionState.Failed);

        if (walletId is not null)
            query = query.Where(e => e.WalletId == walletId);

        return await query
            .Select(e => MapToRecord(e))
            .ToListAsync(cancellationToken);
    }

    private static ExitSession MapToRecord(ExitSessionEntity e) => new(
        e.Id, e.VtxoTxid, (uint)e.VtxoVout, e.WalletId, e.ClaimAddress,
        e.State, e.NextTxIndex, e.ClaimTxid, e.CreatedAt, e.UpdatedAt, e.FailReason, e.RetryCount);
}
