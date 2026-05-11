using Microsoft.EntityFrameworkCore;
using NArk.Storage.EfCore.Entities;

namespace NArk.Storage.EfCore;

public static class ModelBuilderExtensions
{
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
}
