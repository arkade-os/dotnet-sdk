using Microsoft.EntityFrameworkCore;
using NArk.Abstractions.Sync;
using NArk.Storage.EfCore.Entities;

namespace NArk.Storage.EfCore.Storage;

/// <summary>
/// EF Core-backed <see cref="ISyncStateStorage"/>. Persists a single row in the
/// configured <see cref="ArkStorageOptions.SyncStateTable"/> with id
/// <see cref="ArkSyncStateEntity.SingletonId"/>.
/// </summary>
public class EfCoreSyncStateStorage(IArkDbContextFactory dbContextFactory) : ISyncStateStorage
{
    /// <inheritdoc />
    public async Task<DateTimeOffset?> GetLastFullPollAtAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Set<ArkSyncStateEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == ArkSyncStateEntity.SingletonId, cancellationToken);
        return row?.LastFullPollAt;
    }

    /// <inheritdoc />
    public async Task SetLastFullPollAtAsync(DateTimeOffset value, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var set = db.Set<ArkSyncStateEntity>();
        var existing = await set.FirstOrDefaultAsync(e => e.Id == ArkSyncStateEntity.SingletonId, cancellationToken);
        if (existing is null)
        {
            await set.AddAsync(
                new ArkSyncStateEntity { Id = ArkSyncStateEntity.SingletonId, LastFullPollAt = value },
                cancellationToken);
        }
        else
        {
            existing.LastFullPollAt = value;
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}
