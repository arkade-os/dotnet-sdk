using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NArk.ArkadeIntents.Models;
using NArk.Storage.EfCore;
using NArk.Storage.EfCore.Storage;
using NBitcoin;
using NArk.ArkadeIntents;

namespace NArk.Tests;

[TestFixture]
public class EfCoreArkadeIntentStorageTests
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<TestDb> _dbOptions = null!;
    private EfCoreArkadeIntentStorage _storage = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<TestDb>().UseSqlite(_connection).Options;

        using var ctx = new TestDb(_dbOptions);
        ctx.Database.EnsureCreated();

        _storage = new EfCoreArkadeIntentStorage(new TestDbFactory(_dbOptions));
    }

    [TearDown]
    public void TearDown() => _connection.Dispose();

    [Test]
    public async Task Save_ThenQueryByScript_RoundTrips()
    {
        await _storage.SaveArkadeSwapIntent(Intent("tx1", "script1"));

        var loaded = (await _storage.GetArkadeSwapIntents(swapPkScript: "script1")).Single();

        Assert.That(loaded.Id, Is.EqualTo("tx1"));
        Assert.That(loaded.Type, Is.EqualTo(ArkadeSwapIntentType.BtcToAsset));
        Assert.That(loaded.OfferAmount, Is.EqualTo(Money.Satoshis(10_000)));
        Assert.That(loaded.WantAmount, Is.EqualTo(Money.Satoshis(500)));
        Assert.That(loaded.OfferHex, Is.EqualTo("deadbeef"));
        Assert.That(loaded.Status, Is.EqualTo(ArkadeSwapIntentStatus.Pending));
    }

    [Test]
    public async Task TypeAndStatus_ArePersistedAsNames_NotOrdinals()
    {
        // An ordinal is positional. Adding a corridor or a status anywhere but the end of its enum
        // silently reinterprets every row already written — a `BtcToLightning` swap becomes a
        // `LightningToBtc` one with no migration, no error, and nothing in the row to say so. This
        // reads the raw column rather than round-tripping through EF, because a round trip agrees
        // with itself whichever storage type is in use and would pass on the very change it exists
        // to catch.
        await _storage.SaveArkadeSwapIntent(
            Intent("tx1", "script1", status: ArkadeSwapIntentStatus.Claimable));

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT Type, Status FROM ArkadeSwapIntents WHERE Id = 'tx1'";
        await using var reader = await cmd.ExecuteReaderAsync();

        Assert.That(await reader.ReadAsync(), Is.True, "the intent was not written");
        Assert.Multiple(() =>
        {
            Assert.That(reader.GetString(0), Is.EqualTo(nameof(ArkadeSwapIntentType.BtcToAsset)));
            Assert.That(reader.GetString(1), Is.EqualTo(nameof(ArkadeSwapIntentStatus.Claimable)));
        });
    }

    [Test]
    public async Task GetActiveScripts_ReturnsOnlyPendingScripts()
    {
        await _storage.SaveArkadeSwapIntent(Intent("tx1", "pending-script"));
        await _storage.SaveArkadeSwapIntent(Intent("tx2", "done-script", status: ArkadeSwapIntentStatus.Fulfilled));

        var scripts = await ((NArk.Abstractions.Scripts.IActiveScriptsProvider)_storage).GetActiveScripts();

        Assert.That(scripts, Is.EquivalentTo(new[] { "pending-script" }));
    }

    [Test]
    public async Task UpdateStatus_Pending_TransitionsAndRecordsSpentTxid()
    {
        await _storage.SaveArkadeSwapIntent(Intent("tx1", "script1"));

        var ok = await _storage.UpdateStatus("script1", ArkadeSwapIntentStatus.Fulfilled, "spend-tx");

        Assert.That(ok, Is.True);
        var loaded = (await _storage.GetArkadeSwapIntents(swapPkScript: "script1")).Single();
        Assert.That(loaded.Status, Is.EqualTo(ArkadeSwapIntentStatus.Fulfilled));
        Assert.That(loaded.SpentTxid, Is.EqualTo("spend-tx"));
    }

    [Test]
    public async Task UpdateStatus_Claimable_Transitions()
    {
        // A receive swap whose claim window closed mid-flight must be allowed to leave Claimable,
        // or it is watched and retried forever.
        await _storage.SaveArkadeSwapIntent(Intent("tx1", "script1", status: ArkadeSwapIntentStatus.Claimable));

        var ok = await _storage.UpdateStatus("script1", ArkadeSwapIntentStatus.Resolved);

        Assert.That(ok, Is.True);
        var loaded = (await _storage.GetArkadeSwapIntents(swapPkScript: "script1")).Single();
        Assert.That(loaded.Status, Is.EqualTo(ArkadeSwapIntentStatus.Resolved));
    }

    [Test]
    public async Task UpdateStatus_NonPending_IsNoOp()
    {
        await _storage.SaveArkadeSwapIntent(Intent("tx1", "script1", status: ArkadeSwapIntentStatus.Cancelling));

        var ok = await _storage.UpdateStatus("script1", ArkadeSwapIntentStatus.Fulfilled, "spend-tx");

        Assert.That(ok, Is.False);
        var loaded = (await _storage.GetArkadeSwapIntents(swapPkScript: "script1")).Single();
        Assert.That(loaded.Status, Is.EqualTo(ArkadeSwapIntentStatus.Cancelling));
    }

    [Test]
    public async Task Save_And_UpdateStatus_FireSwapsChanged()
    {
        var events = 0;
        _storage.SwapsChanged += (_, _) => events++;

        await _storage.SaveArkadeSwapIntent(Intent("tx1", "script1"));
        await _storage.UpdateStatus("script1", ArkadeSwapIntentStatus.Recoverable);

        Assert.That(events, Is.EqualTo(2));
    }

    private static ArkadeSwapIntent Intent(string id, string pkScript, ArkadeSwapIntentStatus status = ArkadeSwapIntentStatus.Pending) => new()
    {
        Id = id,
        WalletId = "w1",
        Type = ArkadeSwapIntentType.BtcToAsset,
        OfferAmount = Money.Satoshis(10_000),
        WantAmount = Money.Satoshis(500),
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
        SwapPkScript = pkScript,
        SwapAddress = "tark1...",
        OfferHex = "deadbeef",
        FromAssetId = "btc",
        ToAssetId = "asset-x",
    };

    private sealed class TestDb(DbContextOptions<TestDb> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ConfigureArkEntities(o => o.StoreDateTimeOffsetAsTicks = true);
    }

    private sealed class TestDbFactory(DbContextOptions<TestDb> options) : IArkDbContextFactory
    {
        public Task<DbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult<DbContext>(new TestDb(options));
    }

    [Test]
    public async Task AnIdFilter_ReturnsOnlyThatIntent()
    {
        // The lookup that replaced "load every intent and pick in memory". If the filter were
        // ignored the callers would still find their swap — among everyone else's — which is why
        // this asserts the count, not just the hit.
        var mine = Intent("swap-mine", "5120" + new string('a', 64));
        var theirs = Intent("swap-theirs", "5120" + new string('b', 64));
        await _storage.SaveArkadeSwapIntent(mine);
        await _storage.SaveArkadeSwapIntent(theirs);

        var found = await _storage.GetArkadeSwapIntents(id: "swap-mine");

        Assert.That(found.Select(i => i.Id), Is.EqualTo(new[] { "swap-mine" }));
    }

    [Test]
    public async Task TheSingleLookup_FindsNothingForAnUnknownId()
    {
        await _storage.SaveArkadeSwapIntent(Intent("swap-1", "5120" + new string('c', 64)));

        Assert.That(await _storage.GetArkadeSwapIntent("no-such-swap"), Is.Null);
    }
}
