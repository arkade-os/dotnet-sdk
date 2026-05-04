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
    /// Registers all Ark EF Core storage implementations.
    /// The consumer's TDbContext must call modelBuilder.ConfigureArkEntities() in OnModelCreating.
    /// For wallet provider registration, use NArk.Core.Wallet.DefaultWalletProvider separately.
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
    /// Registers opt-in payment-tracking storage and the <see cref="PaymentTrackingService"/>.
    /// Call this in addition to <see cref="AddArkEfCoreStorage{TDbContext}"/> only if the
    /// consumer needs payment tracking. The consumer's TDbContext must also call
    /// <c>modelBuilder.ConfigureArkPaymentEntities()</c> in OnModelCreating, and the DB
    /// schema must include the Payments and PaymentRequests tables (run the corresponding migration).
    /// </summary>
    public static IServiceCollection AddArkPaymentTracking(this IServiceCollection services)
    {
        services.AddSingleton<EfCorePaymentStorage>();
        services.AddSingleton<IPaymentStorage>(sp => sp.GetRequiredService<EfCorePaymentStorage>());

        services.AddSingleton<EfCorePaymentRequestStorage>();
        services.AddSingleton<IPaymentRequestStorage>(sp => sp.GetRequiredService<EfCorePaymentRequestStorage>());

        services.AddSingleton<PaymentTrackingService>();
        services.AddHostedService<PaymentTrackingServiceStarter>();

        return services;
    }

    /// <summary>
    /// Hosted service whose only job is to resolve <see cref="PaymentTrackingService"/> so
    /// its constructor-time event subscriptions fire. Nothing else depends on it in the
    /// container, so without this it would never be instantiated.
    /// </summary>
    private sealed class PaymentTrackingServiceStarter(PaymentTrackingService service) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _ = service;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
