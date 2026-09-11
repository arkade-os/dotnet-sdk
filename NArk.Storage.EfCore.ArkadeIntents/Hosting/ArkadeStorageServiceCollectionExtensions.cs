using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NArk.Abstractions.Scripts;
using NArk.ArkadeIntents;
using NArk.Storage.EfCore.Storage;

namespace NArk.Storage.EfCore.Hosting;

/// <summary>Opt-in registration for Arkade swap persistence.</summary>
public static class ArkadeStorageServiceCollectionExtensions
{
    /// <summary>Registers swap storage after AddArkEfCoreStorage; the context must call ConfigureArkadeEntities.</summary>
    public static IServiceCollection AddArkadeEfCoreStorage(this IServiceCollection services)
    {
        services.TryAddSingleton<EfCoreArkadeIntentStorage>();
        services.TryAddSingleton<IArkadeIntentStorage>(sp => sp.GetRequiredService<EfCoreArkadeIntentStorage>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IActiveScriptsProvider, EfCoreArkadeActiveScriptsProvider>());
        return services;
    }

    private sealed class EfCoreArkadeActiveScriptsProvider(EfCoreArkadeIntentStorage storage) : IActiveScriptsProvider
    {
        public event EventHandler? ActiveScriptsChanged
        {
            add => storage.ActiveScriptsChanged += value;
            remove => storage.ActiveScriptsChanged -= value;
        }

        public Task<HashSet<string>> GetActiveScripts(CancellationToken cancellationToken = default) =>
            ((IActiveScriptsProvider)storage).GetActiveScripts(cancellationToken);
    }
}
