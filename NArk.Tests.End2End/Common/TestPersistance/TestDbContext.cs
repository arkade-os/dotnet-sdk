using Microsoft.EntityFrameworkCore;
using NArk.Storage.EfCore;

namespace NArk.Tests.End2End.TestPersistance;

public class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ConfigureArkEntities();
        // Tests exercise the unilateral-exit storage too — opt in to the
        // exit entities here (they're not part of ConfigureArkEntities by
        // design, so each consumer that uses them registers them explicitly).
        modelBuilder.ConfigureArkExitEntities();
    }
}
