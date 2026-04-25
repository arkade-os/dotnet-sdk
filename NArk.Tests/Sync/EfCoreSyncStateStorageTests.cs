using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NArk.Storage.EfCore;
using NArk.Storage.EfCore.Storage;

namespace NArk.Tests.Sync;

[TestFixture]
public class EfCoreSyncStateStorageTests
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<TestArkDbContext> _dbOptions;
    private EfCoreSyncStateStorage _storage = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<TestArkDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var ctx = new TestArkDbContext(_dbOptions);
        ctx.Database.EnsureCreated();

        _storage = new EfCoreSyncStateStorage(new TestArkDbContextFactory(_dbOptions));
    }

    [TearDown]
    public void TearDown() => _connection.Dispose();

    [Test]
    public async Task GetLastFullPollAt_ReturnsNull_WhenNeverWritten()
    {
        var value = await _storage.GetLastFullPollAtAsync();
        Assert.That(value, Is.Null);
    }

    [Test]
    public async Task SetThenGet_RoundTripsTheTimestamp()
    {
        var ts = new DateTimeOffset(2026, 04, 25, 09, 12, 34, TimeSpan.Zero);
        await _storage.SetLastFullPollAtAsync(ts);

        var read = await _storage.GetLastFullPollAtAsync();
        Assert.That(read, Is.EqualTo(ts));
    }

    [Test]
    public async Task Set_OverwritesExisting()
    {
        var first = DateTimeOffset.UtcNow.AddHours(-1);
        var second = DateTimeOffset.UtcNow;

        await _storage.SetLastFullPollAtAsync(first);
        await _storage.SetLastFullPollAtAsync(second);

        var read = await _storage.GetLastFullPollAtAsync();
        Assert.That(read, Is.EqualTo(second));
    }

    [Test]
    public async Task Set_PersistsAcrossDbContextInstances()
    {
        var ts = DateTimeOffset.UtcNow;
        await _storage.SetLastFullPollAtAsync(ts);

        // New storage instance — fresh DbContext, same SQLite connection.
        var fresh = new EfCoreSyncStateStorage(new TestArkDbContextFactory(_dbOptions));
        var read = await fresh.GetLastFullPollAtAsync();
        Assert.That(read, Is.EqualTo(ts));
    }

    private class TestArkDbContext(DbContextOptions<TestArkDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ConfigureArkEntities();
    }

    private class TestArkDbContextFactory(DbContextOptions<TestArkDbContext> options) : IArkDbContextFactory
    {
        public Task<DbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult<DbContext>(new TestArkDbContext(options));
    }
}
