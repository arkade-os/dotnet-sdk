using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NArk.Abstractions.Contracts;
using NArk.Storage.EfCore;
using NArk.Storage.EfCore.Storage;

namespace NArk.Tests;

/// <summary>
/// Pins the SQLite behaviour of the <c>GetContracts(scope:)</c> filter. Uses a real
/// SQLite provider (not InMemory) on purpose: the bitwise <c>(Scope &amp; s) == s</c>
/// predicate must translate to SQL. InMemory would client-evaluate it and silently
/// hide a non-translatable query (the <c>HasFlag</c>-vs-bitwise trap).
/// </summary>
[TestFixture]
public class EfCoreContractScopeStorageTests
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<TestArkDbContext> _dbOptions = null!;
    private EfCoreContractStorage _storage = null!;

    [SetUp]
    public async Task SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<TestArkDbContext>()
            .UseSqlite(_connection)
            .Options;

        await using (var ctx = new TestArkDbContext(_dbOptions))
        {
            await ctx.Database.EnsureCreatedAsync();
            ctx.Add(new Storage.EfCore.Entities.ArkWalletEntity { Id = "w", Wallet = "mnemonic" });
            await ctx.SaveChangesAsync();
        }

        _storage = new EfCoreContractStorage(new TestArkDbContextFactory(_dbOptions), new ArkStorageOptions());
    }

    [TearDown]
    public void TearDown()
    {
        _connection.Dispose();
    }

    private async Task SeedAsync(string script, ContractScope scope)
    {
        await _storage.SaveContract(new ArkContractEntity(
            Script: script,
            ActivityState: ContractActivityState.Active,
            Type: "t-" + script,
            AdditionalData: new Dictionary<string, string>(),
            WalletIdentifier: "w",
            CreatedAt: DateTimeOffset.UtcNow)
        {
            Scope = scope
        });
    }

    [Test]
    public async Task GetContracts_ScopeOnchain_ReturnsOnlyOnchainAndBoth()
    {
        await SeedAsync("onchain1", ContractScope.Onchain);
        await SeedAsync("offchain1", ContractScope.Offchain);
        await SeedAsync("both1", ContractScope.Onchain | ContractScope.Offchain);

        var result = await _storage.GetContracts(scope: ContractScope.Onchain);

        var scripts = result.Select(c => c.Script).ToHashSet();
        Assert.That(scripts, Does.Contain("onchain1"));
        Assert.That(scripts, Does.Contain("both1"));
        Assert.That(scripts, Does.Not.Contain("offchain1"));
    }

    [Test]
    public async Task GetContracts_ScopeOffchain_ReturnsOnlyOffchainAndBoth()
    {
        await SeedAsync("onchain1", ContractScope.Onchain);
        await SeedAsync("offchain1", ContractScope.Offchain);
        await SeedAsync("both1", ContractScope.Onchain | ContractScope.Offchain);

        var result = await _storage.GetContracts(scope: ContractScope.Offchain);

        var scripts = result.Select(c => c.Script).ToHashSet();
        Assert.That(scripts, Does.Contain("offchain1"));
        Assert.That(scripts, Does.Contain("both1"));
        Assert.That(scripts, Does.Not.Contain("onchain1"));
    }

    [Test]
    public async Task GetContracts_NoScope_ReturnsAll()
    {
        await SeedAsync("onchain1", ContractScope.Onchain);
        await SeedAsync("offchain1", ContractScope.Offchain);

        var result = await _storage.GetContracts();

        var scripts = result.Select(c => c.Script).ToHashSet();
        Assert.That(scripts, Does.Contain("onchain1"));
        Assert.That(scripts, Does.Contain("offchain1"));
    }

    [Test]
    public async Task GetContracts_ScopeFilter_TranslatesToSqlOnSqlite()
    {
        // Real SQLite: if the bitwise predicate didn't translate, this would throw
        // (or, on InMemory, silently client-evaluate). Asserting it runs is the point.
        await SeedAsync("onchain1", ContractScope.Onchain);

        Assert.DoesNotThrowAsync(async () =>
            await _storage.GetContracts(scope: ContractScope.Onchain));
    }

    [Test]
    public async Task GetContracts_RoundTripsScope()
    {
        await SeedAsync("onchain1", ContractScope.Onchain);

        var result = await _storage.GetContracts(scripts: new[] { "onchain1" });
        Assert.That(result.Single().Scope, Is.EqualTo(ContractScope.Onchain));
    }

    private class TestArkDbContext(DbContextOptions<TestArkDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            // SQLite refuses ORDER BY on DateTimeOffset columns; opt into the
            // ticks-based storage (same as the SQLite sample wallet) so GetContracts'
            // OrderByDescending(CreatedAt) runs server-side.
            => modelBuilder.ConfigureArkEntities(o => o.StoreDateTimeOffsetAsTicks = true);
    }

    private class TestArkDbContextFactory(DbContextOptions<TestArkDbContext> options) : IArkDbContextFactory
    {
        public Task<DbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult<DbContext>(new TestArkDbContext(options));
    }
}
