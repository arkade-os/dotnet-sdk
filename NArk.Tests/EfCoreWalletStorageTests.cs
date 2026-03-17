using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NArk.Abstractions.Wallets;
using NArk.Storage.EfCore;
using NArk.Storage.EfCore.Storage;

namespace NArk.Tests;

[TestFixture]
public class EfCoreWalletStorageTests
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<TestArkDbContext> _dbOptions;
    private EfCoreWalletStorage _storage = null!;

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

        _storage = new EfCoreWalletStorage(new TestArkDbContextFactory(_dbOptions));
    }

    [TearDown]
    public void TearDown()
    {
        _connection.Dispose();
    }

    [Test]
    public async Task SaveWallet_DoesNotResetLastUsedIndex_WhenExistingIndexIsHigher()
    {
        var wallet = MakeWallet("w1", lastUsedIndex: 10);
        await _storage.SaveWallet(wallet);

        // Re-import same wallet with lower index (simulates shared mnemonic reimport)
        var reimported = wallet with { LastUsedIndex = 0 };
        await _storage.SaveWallet(reimported);

        var loaded = await _storage.GetWalletById("w1");
        Assert.That(loaded!.LastUsedIndex, Is.EqualTo(10));
    }

    [Test]
    public async Task SaveWallet_AdvancesLastUsedIndex_WhenNewIndexIsHigher()
    {
        var wallet = MakeWallet("w1", lastUsedIndex: 5);
        await _storage.SaveWallet(wallet);

        var updated = wallet with { LastUsedIndex = 12 };
        await _storage.SaveWallet(updated);

        var loaded = await _storage.GetWalletById("w1");
        Assert.That(loaded!.LastUsedIndex, Is.EqualTo(12));
    }

    [Test]
    public async Task UpsertWallet_DoesNotResetLastUsedIndex_WhenExistingIndexIsHigher()
    {
        var wallet = MakeWallet("w1", lastUsedIndex: 10);
        await _storage.UpsertWallet(wallet);

        var reimported = wallet with { LastUsedIndex = 0 };
        await _storage.UpsertWallet(reimported);

        var loaded = await _storage.GetWalletById("w1");
        Assert.That(loaded!.LastUsedIndex, Is.EqualTo(10));
    }

    [Test]
    public async Task UpsertWallet_AdvancesLastUsedIndex_WhenNewIndexIsHigher()
    {
        var wallet = MakeWallet("w1", lastUsedIndex: 5);
        await _storage.UpsertWallet(wallet);

        var updated = wallet with { LastUsedIndex = 12 };
        await _storage.UpsertWallet(updated);

        var loaded = await _storage.GetWalletById("w1");
        Assert.That(loaded!.LastUsedIndex, Is.EqualTo(12));
    }

    private static ArkWalletInfo MakeWallet(string id, int lastUsedIndex = 0) =>
        new(id, "secret-" + id, null, WalletType.HD, null, lastUsedIndex);

    /// <summary>
    /// Minimal DbContext that registers the Ark entity model for testing.
    /// </summary>
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
