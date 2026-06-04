using Microsoft.Extensions.DependencyInjection;
using NArk.Abstractions.Batches;
using NArk.Arkade.Emulator;

namespace NArk.Arkade.Hosting;

/// <summary>
/// DI helpers for wiring an <see cref="IEmulatorProvider"/> into the
/// service container.
/// </summary>
public static class EmulatorServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="EmulatorClient"/> as the application's
    /// <see cref="IEmulatorProvider"/>, configures
    /// <see cref="EmulatorClientOptions"/>, and wires a typed
    /// <see cref="HttpClient"/> via <see cref="IHttpClientFactory"/>.
    /// </summary>
    /// <param name="services">The DI service collection.</param>
    /// <param name="configure">
    /// Setter for <see cref="EmulatorClientOptions"/>; at minimum the
    /// <see cref="EmulatorClientOptions.ServerUrl"/> must be set.
    /// </param>
    public static IServiceCollection AddEmulatorClient(
        this IServiceCollection services,
        Action<EmulatorClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        services.AddHttpClient<EmulatorClient>();
        services.AddSingleton<IEmulatorProvider>(
            sp => sp.GetRequiredService<EmulatorClient>());
        return services;
    }

    /// <summary>
    /// One-liner: registers the emulator REST client AND the
    /// <see cref="ArkadeBatchSessionExtension"/> so any batch that includes
    /// arkade-bound inputs automatically gets emulator co-signing.
    /// Use when you don't care about wiring the two pieces separately.
    /// </summary>
    public static IServiceCollection AddArkadeEmulator(
        this IServiceCollection services,
        Action<EmulatorClientOptions> configure)
    {
        AddEmulatorClient(services, configure);
        services.AddSingleton<IBatchSessionExtension, ArkadeBatchSessionExtension>();
        return services;
    }
}
