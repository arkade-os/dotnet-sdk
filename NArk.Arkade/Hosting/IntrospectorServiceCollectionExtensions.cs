using Microsoft.Extensions.DependencyInjection;
using NArk.Arkade.Introspector;

namespace NArk.Arkade.Hosting;

/// <summary>
/// DI helpers for wiring an <see cref="IIntrospectorProvider"/> into the
/// service container.
/// </summary>
public static class IntrospectorServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IntrospectorClient"/> as the application's
    /// <see cref="IIntrospectorProvider"/>, configures
    /// <see cref="IntrospectorClientOptions"/>, and wires a typed
    /// <see cref="HttpClient"/> via <see cref="IHttpClientFactory"/>.
    /// </summary>
    /// <param name="services">The DI service collection.</param>
    /// <param name="configure">
    /// Setter for <see cref="IntrospectorClientOptions"/>; at minimum the
    /// <see cref="IntrospectorClientOptions.ServerUrl"/> must be set.
    /// </param>
    public static IServiceCollection AddIntrospectorClient(
        this IServiceCollection services,
        Action<IntrospectorClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        services.AddHttpClient<IntrospectorClient>();
        services.AddSingleton<IIntrospectorProvider>(
            sp => sp.GetRequiredService<IntrospectorClient>());
        return services;
    }
}
