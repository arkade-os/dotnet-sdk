using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Payments;
using NArk.Abstractions.Scripts;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Storage.EfCore.Storage;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Services;

namespace NArk.Storage.EfCore.Hosting;

public static class StorageServiceCollectionExtensions
{
    /// <summary>
    /// Registers core Ark EF Core storage implementations.
    /// The consumer's TDbContext must call <c>modelBuilder.ConfigureArkEntities()</c> in OnModelCreating.
    /// For payment tracking, also call <see cref="AddArkPaymentTracking"/>.
    /// </summary>
    public static IServiceCollection AddArkEfCoreStorage<TDbContext>(
        this IServiceCollection services,
        Action<ArkStorageOptions>? configureOptions = null)
        where TDbContext : DbContext
    {
        var options = new ArkStorageOptions();
        configureOptions?.Invoke(options);
        services.AddSingleton(options);

        // Internal DbContext factory adapter
        services.AddSingleton<IArkDbContextFactory, ArkDbContextFactory<TDbContext>>();

        // Storage implementations
        services.AddSingleton<EfCoreVtxoStorage>();
        services.AddSingleton<IVtxoStorage>(sp => sp.GetRequiredService<EfCoreVtxoStorage>());
        services.AddSingleton<IActiveScriptsProvider>(sp => sp.GetRequiredService<EfCoreVtxoStorage>());

        services.AddSingleton<EfCoreContractStorage>();
        services.AddSingleton<IContractStorage>(sp => sp.GetRequiredService<EfCoreContractStorage>());
        services.AddSingleton<IActiveScriptsProvider>(sp => sp.GetRequiredService<EfCoreContractStorage>());

        services.AddSingleton<EfCoreIntentStorage>();
        services.AddSingleton<IIntentStorage>(sp => sp.GetRequiredService<EfCoreIntentStorage>());

        services.AddSingleton<EfCoreSwapStorage>();
        services.AddSingleton<ISwapStorage>(sp => sp.GetRequiredService<EfCoreSwapStorage>());

        services.AddSingleton<EfCoreWalletStorage>();
        services.AddSingleton<IWalletStorage>(sp => sp.GetRequiredService<EfCoreWalletStorage>());

        return services;
    }

    /// <summary>
    /// Registers payment tracking storage, and the <see cref="PaymentTrackingService"/> hosted service
    /// that automatically updates payment statuses from protocol events.
    /// <para>
    /// Requires <see cref="AddArkEfCoreStorage{TDbContext}"/> to be called first.
    /// The consumer's DbContext must also call <c>modelBuilder.ConfigureArkPaymentEntities()</c>.
    /// </para>
    /// </summary>
    public static IServiceCollection AddArkPaymentTracking(this IServiceCollection services)
    {
        services.AddSingleton<EfCorePaymentStorage>();
        services.AddSingleton<IPaymentStorage>(sp => sp.GetRequiredService<EfCorePaymentStorage>());

        services.AddSingleton<EfCorePaymentRequestStorage>();
        services.AddSingleton<IPaymentRequestStorage>(sp => sp.GetRequiredService<EfCorePaymentRequestStorage>());

        services.AddHostedService<PaymentTrackingService>();

        return services;
    }
}
