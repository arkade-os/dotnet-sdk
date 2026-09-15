using Microsoft.EntityFrameworkCore;
using NArk.Storage.EfCore.Entities;

namespace NArk.Storage.EfCore;

/// <summary>Opt-in mappings for Arkade swap storage.</summary>
public static class ArkadeModelBuilderExtensions
{
    /// <summary>Configures swap tables alongside the core mappings, honoring the storage timestamp option.</summary>
    public static ModelBuilder ConfigureArkadeEntities(
        this ModelBuilder modelBuilder, Action<ArkStorageOptions>? configure = null)
    {
        var options = new ArkStorageOptions();
        configure?.Invoke(options);
        var entity = modelBuilder.Entity<ArkadeSwapIntentEntity>();
        ArkadeSwapIntentEntity.Configure(entity, options);
        if (options.Schema is not null)
            entity.ToTable("ArkadeSwapIntents", options.Schema);
        if (options.StoreDateTimeOffsetAsTicks)
            entity.Property(e => e.CreatedAt).HasConversion(
                value => value.UtcTicks, ticks => new DateTimeOffset(ticks, TimeSpan.Zero));
        return modelBuilder;
    }
}
