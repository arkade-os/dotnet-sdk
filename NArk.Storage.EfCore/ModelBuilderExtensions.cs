using Microsoft.EntityFrameworkCore;
using NArk.Storage.EfCore.Entities;

namespace NArk.Storage.EfCore;

public static class ModelBuilderExtensions
{
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

        return modelBuilder;
    }
}
