using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NArk.Abstractions.Contracts;
using NArk.Storage.EfCore;
using NArk.Storage.EfCore.Entities;
using NArk.Storage.EfCore.Storage;

namespace NArk.Tests;

[TestFixture]
public class EfCoreContractStorageTests
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<TestArkDbContext> _plainOptions = null!;

    private const string WalletId = "w1";
    private const string Script = "ab" + "cd";

    [SetUp]
    public void SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _plainOptions = new DbContextOptionsBuilder<TestArkDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var ctx = new TestArkDbContext(_plainOptions);
        ctx.Database.EnsureCreated();

        // Parent wallet row so the contract FK (WalletId -> Wallets) is satisfiable.
        ctx.Set<ArkWalletEntity>().Add(new ArkWalletEntity { Id = WalletId });
        ctx.SaveChanges();
    }

    [TearDown]
    public void TearDown() => _connection.Dispose();

    [Test]
    public async Task SaveContract_UpdatesExistingRow_WhenAlreadyPresent()
    {
        var storage = new EfCoreContractStorage(new TestArkDbContextFactory(_plainOptions), new ArkStorageOptions());

        // First save inserts (Inactive), second updates to Active with new metadata.
        await storage.SaveContract(Contract(ContractActivityState.Inactive, source: "first"));
        await storage.SaveContract(Contract(ContractActivityState.Active, source: "second"));

        var row = await LoadRowAsync();
        Assert.That(row, Is.Not.Null);
        Assert.That(row!.ActivityState, Is.EqualTo(ContractActivityState.Active));
        Assert.That(row.Metadata!["Source"], Is.EqualTo("second"));
    }

    [Test]
    public async Task SaveContract_ToleratesLostInsertRace_AndAppliesUpdate()
    {
        // Simulate the lost first-insert race: the SUT reads (no row), decides to INSERT, and right
        // before its INSERT hits the DB an interceptor commits a conflicting {Script, WalletId} row
        // via a sibling context. The SUT's insert then hits a real PK unique violation; the fix must
        // catch it, re-read the now-existing row, and apply the update path instead of throwing.
        var conflictInjector = new ConflictInjectingInterceptor(_plainOptions, WalletId, Script);

        var racingOptions = new DbContextOptionsBuilder<TestArkDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(conflictInjector)
            .Options;

        var storage = new EfCoreContractStorage(
            new TestArkDbContextFactory(racingOptions), new ArkStorageOptions());

        var changedFired = 0;
        storage.ContractsChanged += (_, _) => changedFired++;

        // The "winning" writer inserted an Inactive row with Source="winner"; our SUT call carries
        // Active + Source="loser" and must convert to an update.
        Assert.DoesNotThrowAsync(() =>
            storage.SaveContract(Contract(ContractActivityState.Active, source: "loser")));

        var row = await LoadRowAsync();
        Assert.That(row, Is.Not.Null, "the row must exist after the race");
        Assert.That(row!.ActivityState, Is.EqualTo(ContractActivityState.Active),
            "the losing insert must convert to an update reflecting the second call");
        Assert.That(row.Metadata!["Source"], Is.EqualTo("loser"));
        Assert.That(conflictInjector.Injected, Is.True, "the race must actually have been injected");
        Assert.That(changedFired, Is.GreaterThanOrEqualTo(1), "ContractsChanged should fire for the effective change");
    }

    private static ArkContractEntity Contract(ContractActivityState state, string source) =>
        new(
            Script: Script,
            ActivityState: state,
            Type: "Payment",
            AdditionalData: new Dictionary<string, string> { ["k"] = "v" },
            WalletIdentifier: WalletId,
            CreatedAt: DateTimeOffset.UtcNow)
        {
            Metadata = new Dictionary<string, string> { ["Source"] = source },
        };

    private async Task<ArkWalletContractEntity?> LoadRowAsync()
    {
        await using var ctx = new TestArkDbContext(_plainOptions);
        return await ctx.Set<ArkWalletContractEntity>().AsNoTracking()
            .FirstOrDefaultAsync(c => c.WalletId == WalletId && c.Script == Script);
    }

    /// <summary>
    /// On the first save that adds the target contract, commits a conflicting row through a sibling
    /// context (no interceptor, so no recursion) to force a real unique-constraint violation on the
    /// intercepted save. Fires exactly once.
    /// </summary>
    private sealed class ConflictInjectingInterceptor(
        DbContextOptions<TestArkDbContext> plainOptions, string walletId, string script)
        : SaveChangesInterceptor
    {
        public bool Injected { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!Injected && AddsTargetContract(eventData))
            {
                Injected = true;
                await using var sibling = new TestArkDbContext(plainOptions);
                sibling.Set<ArkWalletContractEntity>().Add(new ArkWalletContractEntity
                {
                    Script = script,
                    WalletId = walletId,
                    ActivityState = ContractActivityState.Inactive,
                    Type = "Payment",
                    ContractData = new Dictionary<string, string>(),
                    Metadata = new Dictionary<string, string> { ["Source"] = "winner" },
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                await sibling.SaveChangesAsync(cancellationToken);
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private bool AddsTargetContract(DbContextEventData eventData) =>
            eventData.Context is not null
            && eventData.Context.ChangeTracker.Entries<ArkWalletContractEntity>().Any(e =>
                e.State == EntityState.Added
                && e.Entity.Script == script
                && e.Entity.WalletId == walletId);
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
