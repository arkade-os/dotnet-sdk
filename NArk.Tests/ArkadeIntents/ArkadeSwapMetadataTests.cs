using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Models;
using NArk.Storage.EfCore;
using NArk.Storage.EfCore.Storage;
using NBitcoin;

namespace NArk.Tests.ArkadeIntents;

/// <summary>
/// The corridor-specific half of a swap intent: one JSON column, read and written through typed
/// views rather than through string keys at each call site.
/// </summary>
[TestFixture]
public class ArkadeSwapMetadataTests
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
    public async Task AnOffBoardsMetadata_SurvivesTheRoundTrip()
    {
        var intent = Intent(ArkadeSwapIntentType.BtcToOnchain)
            .WithOnchainMetadata(new OnchainSwapMetadata(
                Preimage: new string('a', 64),
                HtlcPubkey: new string('b', 64),
                HtlcLocktime: 1_800_000_000,
                PayoutAddress: "bcrt1qpayout"));

        await _storage.SaveArkadeSwapIntent(intent);
        var loaded = (await _storage.GetArkadeSwapIntents(id: "swap-1")).Single();

        Assert.That(loaded.OnchainMetadata(), Is.EqualTo(intent.OnchainMetadata()));
    }

    [Test]
    public async Task AMutationThroughTheView_IsActuallySaved()
    {
        // The trap the value comparer exists for. EF compares a dictionary property by REFERENCE
        // unless told otherwise, so an in-place edit of a loaded row leaves it looking unchanged —
        // SaveChanges writes nothing, returns success, and the edit is gone at the next read.
        await _storage.SaveArkadeSwapIntent(
            Intent(ArkadeSwapIntentType.LightningToBtc)
                .WithLightningMetadata(new LightningSwapMetadata("lnbcrt1...", null)));

        var loaded = (await _storage.GetArkadeSwapIntents(id: "swap-1")).Single();
        loaded.WithLightningMetadata(new LightningSwapMetadata("lnbcrt1...", new string('c', 64)));
        await _storage.SaveArkadeSwapIntent(loaded);

        var reloaded = (await _storage.GetArkadeSwapIntents(id: "swap-1")).Single();
        Assert.That(reloaded.LightningMetadata().Preimage, Is.EqualTo(new string('c', 64)));
    }

    [Test]
    public async Task TheStoredIntentsBlob_IsNotSharedWithTheChangeTracker()
    {
        // A loaded intent hands out the entity's own dictionary if the mapper does not copy it, and
        // a caller editing it would then be editing a tracked row outside any save.
        await _storage.SaveArkadeSwapIntent(
            Intent(ArkadeSwapIntentType.LightningToBtc)
                .WithLightningMetadata(new LightningSwapMetadata("lnbcrt1...", null)));

        var loaded = (await _storage.GetArkadeSwapIntents(id: "swap-1")).Single();
        loaded.Metadata[ArkadeSwapMetadataKeys.Invoice] = "tampered";

        var reloaded = (await _storage.GetArkadeSwapIntents(id: "swap-1")).Single();
        Assert.That(reloaded.LightningMetadata().Invoice, Is.EqualTo("lnbcrt1..."));
    }

    [Test]
    public void ANullValue_ClearsTheKeyRatherThanStoringAnEmptyOne()
    {
        // "this corridor has no such value" and "it has an empty one" are different, and only the
        // first should read back as absent.
        var intent = Intent(ArkadeSwapIntentType.LightningToBtc)
            .WithLightningMetadata(new LightningSwapMetadata("lnbcrt1...", new string('c', 64)))
            .WithLightningMetadata(new LightningSwapMetadata("lnbcrt1...", null));

        Assert.Multiple(() =>
        {
            Assert.That(intent.Metadata.ContainsKey(ArkadeSwapMetadataKeys.Preimage), Is.False);
            Assert.That(intent.LightningMetadata().Preimage, Is.Null);
        });
    }

    [Test]
    public void ReadingAnotherCorridorsView_IsRefusedRatherThanAnsweredWithNulls()
    {
        // Asking a Lightning swap for its L1 HTLC terms is not an empty result, it is the wrong
        // question. Answering it with a record full of nulls moves the failure several frames away
        // from the call site that got it wrong.
        var intent = Intent(ArkadeSwapIntentType.LightningToBtc)
            .WithLightningMetadata(new LightningSwapMetadata("lnbcrt1...", null));

        var ex = Assert.Throws<InvalidOperationException>(() => intent.OnchainMetadata());

        Assert.That(ex!.Message, Does.Contain("LightningToBtc"));
    }

    [Test]
    public void AnAssetSwapWithNoOffer_SaysSoRatherThanFailingAtTheCancel()
    {
        // Without the offer TLV the covenant cannot be rebuilt, so the cancel path is unreachable.
        // Named here rather than at the Convert.FromHexString that would otherwise hit it.
        var intent = Intent(ArkadeSwapIntentType.BtcToAsset);

        var ex = Assert.Throws<InvalidOperationException>(() => intent.AssetMetadata());

        Assert.That(ex!.Message, Does.Contain("no offer"));
    }

    private static ArkadeSwapIntent Intent(ArkadeSwapIntentType type) => new()
    {
        Id = "swap-1",
        WalletId = "w1",
        Type = type,
        OfferAmount = Money.Satoshis(10_000),
        WantAmount = Money.Satoshis(9_750),
        Status = ArkadeSwapIntentStatus.Pending,
        CreatedAt = DateTimeOffset.UtcNow,
        SwapPkScript = "5120aa",
        SwapAddress = "tark1...",
    };

    private sealed class TestDb(DbContextOptions<TestDb> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ConfigureArkEntities(o => o.StoreDateTimeOffsetAsTicks = true);
    }

    private sealed class TestDbFactory(DbContextOptions<TestDb> options) : IArkDbContextFactory
    {
        public Task<DbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<DbContext>(new TestDb(options));
    }
}
