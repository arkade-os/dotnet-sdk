using Microsoft.EntityFrameworkCore;
using NArk.Storage.EfCore;

namespace NArk.Wallet.Client.Services;

public class WalletDbContext(DbContextOptions<WalletDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ConfigureArkEntities();
    }
}
