using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NArk.ArkadeIntents;
using NArk.Storage.EfCore;
using NArk.Storage.EfCore.Entities;
using NArk.Storage.EfCore.Hosting;

namespace NArk.Tests.ArkadeIntents;

public class OptionalArkadeStorageTests
{
    [Test]
    public void CoreModel_DoesNotCreateSwapTables()
    {
        using var db = new CoreDb(new DbContextOptionsBuilder<CoreDb>().UseSqlite("Data Source=:memory:").Options);
        Assert.That(db.Model.FindEntityType(typeof(ArkadeSwapIntentEntity)), Is.Null);
    }

    [Test]
    public void CoreRegistration_DoesNotRequireSwapStorage()
    {
        var services = new ServiceCollection().AddArkEfCoreStorage<CoreDb>();
        Assert.That(services.Any(d => d.ServiceType == typeof(IArkadeIntentStorage)), Is.False);
    }

    [Test]
    public void CorePackage_DoesNotLoadSwapImplementation()
    {
        var dependencies = typeof(ArkStorageOptions).Assembly.GetReferencedAssemblies();
        Assert.That(dependencies.Select(a => a.Name), Does.Not.Contain("NArk.ArkadeIntents"));
    }

    private sealed class CoreDb(DbContextOptions<CoreDb> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ConfigureArkEntities();
    }
}
