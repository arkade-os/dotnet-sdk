using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NArk.Storage.EfCore;
using NArk.Storage.EfCore.Entities;

namespace NArk.Tests;

/// <summary>
/// Pins the SQLite behaviour for the <see cref="ArkStorageOptions.StoreDateTimeOffsetAsTicks"/>
/// opt-in: with it on, paged storage queries that <c>ORDER BY</c> a
/// <see cref="DateTimeOffset"/> column run cleanly on SQLite. Round-trip strips the original
/// offset (read-back is always UTC) — also pinned here so the trade-off is explicit.
/// </summary>
[TestFixture]
public class EfCoreSqliteOrderByTests
{
    [Test]
    public async Task TicksMapping_OrderByDateTimeOffset_WorksOnSqlite()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<TicksMappingContext>()
            .UseSqlite(connection)
            .Options;

        using (var ctx = new TicksMappingContext(options))
        {
            ctx.Database.EnsureCreated();

            ctx.Add(new ArkWalletEntity { Id = "w", Wallet = "mnemonic" });

            var t0 = DateTimeOffset.UtcNow.AddHours(-2);
            ctx.Add(new ArkWalletContractEntity { Script = "s1", WalletId = "w", Type = "t", CreatedAt = t0 });
            ctx.Add(new ArkWalletContractEntity { Script = "s2", WalletId = "w", Type = "t", CreatedAt = t0.AddHours(1) });
            ctx.Add(new ArkWalletContractEntity { Script = "s3", WalletId = "w", Type = "t", CreatedAt = t0.AddHours(2) });
            await ctx.SaveChangesAsync();
        }

        await using var query = new TicksMappingContext(options);
        var ordered = await query.Set<ArkWalletContractEntity>()
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => c.Script)
            .ToListAsync();

        Assert.That(ordered, Is.EqualTo(new[] { "s3", "s2", "s1" }));
    }

    [Test]
    public async Task TicksMapping_RoundTrip_StripsOffsetToUtc()
    {
        // Documented trade-off: round-trip drops the original offset (read-back is UTC).
        // Consumers that need the original zoned moment must leave the opt-in off and live
        // with the SQLite ORDER BY limitation.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<TicksMappingContext>()
            .UseSqlite(connection)
            .Options;

        var originalOffset = TimeSpan.FromHours(5);
        var written = new DateTimeOffset(2026, 1, 15, 12, 0, 0, originalOffset);

        using (var ctx = new TicksMappingContext(options))
        {
            ctx.Database.EnsureCreated();
            ctx.Add(new ArkWalletEntity { Id = "w", Wallet = "mnemonic" });
            ctx.Add(new ArkWalletContractEntity { Script = "s", WalletId = "w", Type = "t", CreatedAt = written });
            await ctx.SaveChangesAsync();
        }

        await using var read = new TicksMappingContext(options);
        var loaded = await read.Set<ArkWalletContractEntity>().SingleAsync();

        Assert.That(loaded.CreatedAt.UtcDateTime, Is.EqualTo(written.UtcDateTime));
        Assert.That(loaded.CreatedAt.Offset, Is.EqualTo(TimeSpan.Zero));
    }

    [Test]
    public async Task DefaultMapping_StillWorksForReadAndWrite()
    {
        // Backwards-compat: when the opt-in is off, DateTimeOffset is still stored and
        // read back correctly. Only ORDER BY on those columns breaks on SQLite — and
        // that's the bug the opt-in fixes.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<DefaultMappingContext>()
            .UseSqlite(connection)
            .Options;

        var written = DateTimeOffset.UtcNow;
        using (var ctx = new DefaultMappingContext(options))
        {
            ctx.Database.EnsureCreated();
            ctx.Add(new ArkWalletEntity { Id = "w", Wallet = "mnemonic" });
            ctx.Add(new ArkWalletContractEntity { Script = "s", WalletId = "w", Type = "t", CreatedAt = written });
            await ctx.SaveChangesAsync();
        }

        await using var read = new DefaultMappingContext(options);
        var loaded = await read.Set<ArkWalletContractEntity>().SingleAsync();
        Assert.That(loaded.CreatedAt.UtcDateTime, Is.EqualTo(written.UtcDateTime).Within(TimeSpan.FromMilliseconds(1)));
    }

    private class DefaultMappingContext(DbContextOptions<DefaultMappingContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ConfigureArkEntities();
    }

    private class TicksMappingContext(DbContextOptions<TicksMappingContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ConfigureArkEntities(o => o.StoreDateTimeOffsetAsTicks = true);
    }
}
