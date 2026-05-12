using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NArk.Storage.EfCore.Entities;

namespace NArk.Storage.EfCore;

public static class ModelBuilderExtensions
{
    // DateTimeOffset → long (UTC ticks). Applied to every DateTimeOffset property across all
    // Ark entities. Two reasons:
    //
    // 1) EF Core's SQLite provider rejects `ORDER BY` on `DateTimeOffset` columns because the
    //    default TEXT representation is not chronologically sortable across different offsets.
    //    Storing as INTEGER (long ticks) is natively orderable on SQLite — no fallback to
    //    client-side evaluation needed for paged queries.
    //
    // 2) Round-trippable and indexable across all providers (BIGINT on Postgres/MSSQL,
    //    INTEGER on SQLite). Same on-disk size as the native types.
    //
    // Cost: the round-trip strips the original offset (always reads back as UTC, offset zero).
    // The Ark entities use these columns as instants ("when did this happen") rather than zoned
    // moments, so the offset isn't load-bearing.
    private static readonly ValueConverter<DateTimeOffset, long> DateTimeOffsetToTicks =
        new(dto => dto.UtcTicks,
            ticks => new DateTimeOffset(ticks, TimeSpan.Zero));

    private static readonly ValueConverter<DateTimeOffset?, long?> NullableDateTimeOffsetToTicks =
        new(dto => dto.HasValue ? dto.Value.UtcTicks : (long?)null,
            ticks => ticks.HasValue ? new DateTimeOffset(ticks.Value, TimeSpan.Zero) : null);

    /// <summary>
    /// Configures core Ark SDK entity types on the given ModelBuilder.
    /// Call this from your DbContext's OnModelCreating.
    /// For payment tracking tables, also call <see cref="ConfigureArkPaymentEntities"/>.
    /// </summary>
    public static ModelBuilder ConfigureArkEntities(
        this ModelBuilder modelBuilder,
        Action<ArkStorageOptions>? configure = null)
    {
        var options = new ArkStorageOptions();
        configure?.Invoke(options);

        if (options.Schema is not null)
            modelBuilder.HasDefaultSchema(options.Schema);

        ArkWalletEntity.Configure(modelBuilder.Entity<ArkWalletEntity>(), options);
        ArkWalletContractEntity.Configure(modelBuilder.Entity<ArkWalletContractEntity>(), options);
        VtxoEntity.Configure(modelBuilder.Entity<VtxoEntity>(), options);
        ArkIntentEntity.Configure(modelBuilder.Entity<ArkIntentEntity>(), options);
        ArkIntentVtxoEntity.Configure(modelBuilder.Entity<ArkIntentVtxoEntity>(), options);
        ArkSwapEntity.Configure(modelBuilder.Entity<ArkSwapEntity>(), options);

        if (options.StoreDateTimeOffsetAsTicks)
            ApplyDateTimeOffsetTicksConversion(modelBuilder);

        return modelBuilder;
    }

    /// <summary>
    /// Configures payment-tracking entity types (Payments and PaymentRequests tables).
    /// Call this from your DbContext's OnModelCreating alongside <see cref="ConfigureArkEntities"/>
    /// only if you also call <c>AddArkPaymentTracking</c> on the service collection.
    /// Requires <see cref="ConfigureArkEntities"/> to be called first (for the Wallet FK).
    /// </summary>
    public static ModelBuilder ConfigureArkPaymentEntities(
        this ModelBuilder modelBuilder,
        Action<ArkStorageOptions>? configure = null)
    {
        var options = new ArkStorageOptions();
        configure?.Invoke(options);

        ArkPaymentEntity.Configure(modelBuilder.Entity<ArkPaymentEntity>(), options);
        ArkPaymentRequestEntity.Configure(modelBuilder.Entity<ArkPaymentRequestEntity>(), options);

        if (options.StoreDateTimeOffsetAsTicks)
            ApplyDateTimeOffsetTicksConversion(modelBuilder);

        return modelBuilder;
    }

    // Restricted to Ark-owned entities so the converter never silently leaks onto a
    // consumer's own entities sharing the same DbContext. Idempotent: calling
    // ConfigureArkEntities AND ConfigureArkPaymentEntities with the flag won't apply
    // the converter twice (SetValueConverter on an already-converted property is a no-op,
    // but the type-filter short-circuits the second pass cleanly anyway).
    private static readonly IReadOnlySet<Type> ArkOwnedEntityTypes = new HashSet<Type>
    {
        typeof(ArkWalletEntity),
        typeof(ArkWalletContractEntity),
        typeof(VtxoEntity),
        typeof(ArkIntentEntity),
        typeof(ArkIntentVtxoEntity),
        typeof(ArkSwapEntity),
        typeof(ArkPaymentEntity),
        typeof(ArkPaymentRequestEntity),
    };

    private static void ApplyDateTimeOffsetTicksConversion(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!ArkOwnedEntityTypes.Contains(entityType.ClrType))
                continue;

            foreach (var property in entityType.GetProperties())
            {
                if (property.GetValueConverter() is not null)
                    continue;

                if (property.ClrType == typeof(DateTimeOffset))
                    property.SetValueConverter(DateTimeOffsetToTicks);
                else if (property.ClrType == typeof(DateTimeOffset?))
                    property.SetValueConverter(NullableDateTimeOffsetToTicks);
            }
        }
    }
}
