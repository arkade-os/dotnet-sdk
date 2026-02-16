using Microsoft.EntityFrameworkCore;

namespace NArk.Storage.EfCore;

public interface IArkDbContextFactory
{
    Task<DbContext> CreateDbContextAsync(CancellationToken ct = default);
}

public class ArkDbContextFactory<TDbContext>(IDbContextFactory<TDbContext> inner) : IArkDbContextFactory
    where TDbContext : DbContext
{
    public async Task<DbContext> CreateDbContextAsync(CancellationToken ct = default)
        => await inner.CreateDbContextAsync(ct);
}
