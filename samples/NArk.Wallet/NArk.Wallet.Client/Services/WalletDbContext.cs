using Microsoft.EntityFrameworkCore;
using NArk.Storage.EfCore;

namespace NArk.Wallet.Client.Services;

public class WalletDbContext(DbContextOptions<WalletDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // This is a SQLite-backed wallet — opt into the ticks-based DateTimeOffset
        // storage so paged queries that ORDER BY a DateTimeOffset column (GetVtxos,
        // GetIntents, etc.) work. See docs/articles/storage.md for the trade-offs.
        modelBuilder.ConfigureArkEntities(o => o.StoreDateTimeOffsetAsTicks = true);
        modelBuilder.ConfigureArkPaymentEntities(o => o.StoreDateTimeOffsetAsTicks = true);
        modelBuilder.ConfigureArkExitEntities(o => o.StoreDateTimeOffsetAsTicks = true);
    }
}
