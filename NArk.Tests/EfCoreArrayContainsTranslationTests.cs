using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NArk.Abstractions.Intents;
using NArk.Storage.EfCore;
using NArk.Storage.EfCore.Storage;

namespace NArk.Tests;

/// <summary>
/// Pins the storage-query behaviour that broke the first net10 attempt (PR #115).
/// Under C# 14 first-class spans, an <c>array.Contains(x)</c> filter inside an
/// expression tree binds to <c>MemoryExtensions.Contains(ReadOnlySpan&lt;T&gt;, T)</c>
/// instead of <c>Enumerable.Contains</c>. EF Core 8 could not funcletize that node and
/// threw <c>ArgumentException: GenericArguments[1], &apos;System.ReadOnlySpan`1[...]&apos;
/// ... violates the constraint of type parameter &apos;TRet&apos;</c> from the LINQ
/// expression interpreter. EF Core 10 handles it; this test fails again if the
/// EF Core reference is ever rolled back.
/// </summary>
[TestFixture]
public class EfCoreArrayContainsTranslationTests
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<SqliteArkDbContext> _options = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<SqliteArkDbContext>().UseSqlite(_connection).Options;
        using var ctx = new SqliteArkDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown() => _connection.Dispose();

    [Test]
    public async Task GetIntents_WithEnumStateArrayFilter_IsTranslatable()
    {
        var storage = new EfCoreIntentStorage(new SqliteArkDbContextFactory(_options));

        var result = await storage.GetIntents(
            states: [ArkIntentState.WaitingToSubmit, ArkIntentState.WaitingForBatch]);

        Assert.That(result, Is.Empty);
    }

    public class SqliteArkDbContext(DbContextOptions<SqliteArkDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ConfigureArkEntities(o => o.StoreDateTimeOffsetAsTicks = true);
    }

    private class SqliteArkDbContextFactory(DbContextOptions<SqliteArkDbContext> options) : IArkDbContextFactory
    {
        public Task<DbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult<DbContext>(new SqliteArkDbContext(options));
    }
}
