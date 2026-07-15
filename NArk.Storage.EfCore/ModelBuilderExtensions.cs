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
    /// For unilateral-exit tables, also call <see cref="ConfigureArkExitEntities"/>.
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
        SwapIntentEntity.Configure(modelBuilder.Entity<SwapIntentEntity>(), options);

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

    /// <summary>
    /// Configures unilateral-exit entity types (VirtualTxs, VtxoBranches, ExitSessions tables).
    /// Call this from your DbContext's OnModelCreating alongside <see cref="ConfigureArkEntities"/>
    /// only if you also call <c>AddUnilateralExit</c> on the service collection.
    /// Consumers that never plan to drive a unilateral exit shouldn't pay the schema cost
    /// for these tables — keep this call out of OnModelCreating and the corresponding
    /// migration steps drop out automatically.
    /// </summary>
    public static ModelBuilder ConfigureArkExitEntities(
        this ModelBuilder modelBuilder,
        Action<ArkStorageOptions>? configure = null)
    {
        var options = new ArkStorageOptions();
        configure?.Invoke(options);

        VirtualTxEntity.Configure(modelBuilder.Entity<VirtualTxEntity>(), options);
        VtxoBranchEntity.Configure(modelBuilder.Entity<VtxoBranchEntity>(), options);
        ExitSessionEntity.Configure(modelBuilder.Entity<ExitSessionEntity>(), options);

        // Same SQLite-ORDER BY rationale as the core + payment entities: the exit
        // tables carry CreatedAt / UpdatedAt columns that SQLite refuses to sort
        // without the ticks converter. Honour the same option flag for consistency.
        if (options.StoreDateTimeOffsetAsTicks)
            ApplyDateTimeOffsetTicksConversion(modelBuilder);

        return modelBuilder;
    }

    // Restricted to Ark-owned entities so the converter never silently leaks onto a
    // consumer's own entities sharing the same DbContext. Idempotent: calling
    // ConfigureArkEntities AND ConfigureArkPaymentEntities AND ConfigureArkExitEntities
    // with the flag won't apply the converter twice (SetValueConverter on an
    // already-converted property is a no-op, and the GetValueConverter check below
    // short-circuits cleanly anyway).
    private static readonly IReadOnlySet<Type> ArkOwnedEntityTypes = new HashSet<Type>
    {
        typeof(ArkWalletEntity),
        typeof(ArkWalletContractEntity),
        typeof(VtxoEntity),
        typeof(ArkIntentEntity),
        typeof(ArkIntentVtxoEntity),
        typeof(ArkSwapEntity),
        typeof(SwapIntentEntity),
        typeof(ArkPaymentEntity),
        typeof(ArkPaymentRequestEntity),
        typeof(VirtualTxEntity),
        typeof(VtxoBranchEntity),
        typeof(ExitSessionEntity),
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
