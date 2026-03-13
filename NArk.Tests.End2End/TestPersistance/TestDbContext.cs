using Microsoft.EntityFrameworkCore;
using NArk.Storage.EfCore;

namespace NArk.Tests.End2End.TestPersistance;

public class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ConfigureArkEntities();
    }
}
