using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Scripts;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Storage.EfCore.Storage;
using NArk.Swaps.Abstractions;

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
}
