using Microsoft.EntityFrameworkCore;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VTXOs;
using NArk.Storage.EfCore;
using NArk.Storage.EfCore.Hosting;
using NArk.Swaps.Abstractions;

namespace NArk.Tests.End2End.TestPersistance;

/// <summary>
/// Creates EF Core InMemory-backed storage instances for E2E tests.
/// All storage types within a single TestStorage share the same in-memory database.
/// </summary>
internal class TestStorage : IDisposable
{
    private readonly ServiceProvider _serviceProvider;

    public IVtxoStorage VtxoStorage { get; }
    public IContractStorage ContractStorage { get; }
    public IIntentStorage IntentStorage { get; }
    public ISwapStorage SwapStorage { get; }

    public TestStorage(ISafetyService safetyService)
    {
        var dbName = $"Test_{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContextFactory<TestDbContext>(options =>
            options.UseInMemoryDatabase(dbName));
        services.AddSingleton(safetyService);
        services.AddArkEfCoreStorage<TestDbContext>();
        _serviceProvider = services.BuildServiceProvider();

        VtxoStorage = _serviceProvider.GetRequiredService<IVtxoStorage>();
        ContractStorage = _serviceProvider.GetRequiredService<IContractStorage>();
        IntentStorage = _serviceProvider.GetRequiredService<IIntentStorage>();
        SwapStorage = _serviceProvider.GetRequiredService<ISwapStorage>();
    }

    /// <summary>
    /// Creates a standalone intent storage backed by its own in-memory database.
    /// Use when a test needs an intent storage independent of the main storage.
    /// </summary>
    public static IIntentStorage CreateIntentStorage()
    {
        var dbName = $"Test_{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContextFactory<TestDbContext>(options =>
            options.UseInMemoryDatabase(dbName));
        services.AddArkEfCoreStorage<TestDbContext>();
        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IIntentStorage>();
    }

    /// <summary>
    /// Creates a standalone swap storage backed by its own in-memory database.
    /// Use when a test needs a swap storage independent of the main storage.
    /// </summary>
    public static ISwapStorage CreateSwapStorage()
    {
        var dbName = $"Test_{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContextFactory<TestDbContext>(options =>
            options.UseInMemoryDatabase(dbName));
        services.AddArkEfCoreStorage<TestDbContext>();
        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<ISwapStorage>();
    }

    /// <summary>
    /// Clears all swap records from the database. Used for testing swap restoration.
    /// </summary>
    public async Task ClearSwaps()
    {
        var factory = _serviceProvider.GetRequiredService<IArkDbContextFactory>();
        await using var db = await factory.CreateDbContextAsync();
        var swapEntities = db.Set<Storage.EfCore.Entities.ArkSwapEntity>();
        swapEntities.RemoveRange(swapEntities);
        await db.SaveChangesAsync();
    }

    public void Dispose() => _serviceProvider.Dispose();
}
