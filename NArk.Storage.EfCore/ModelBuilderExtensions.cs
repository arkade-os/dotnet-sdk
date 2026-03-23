using Microsoft.EntityFrameworkCore;
using NArk.Storage.EfCore.Entities;

namespace NArk.Storage.EfCore;

public static class ModelBuilderExtensions
{
    /// <summary>
    /// Configures all Ark SDK entity types on the given ModelBuilder.
    /// Call this from your DbContext's OnModelCreating.
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
        VirtualTxEntity.Configure(modelBuilder.Entity<VirtualTxEntity>(), options);
        VtxoBranchEntity.Configure(modelBuilder.Entity<VtxoBranchEntity>(), options);
        ExitSessionEntity.Configure(modelBuilder.Entity<ExitSessionEntity>(), options);

        return modelBuilder;
    }
}
