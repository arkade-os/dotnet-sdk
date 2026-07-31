using Microsoft.Extensions.DependencyInjection;
using NArk.Arkade.Covclaim;

namespace NArk.Arkade.Hosting;

/// <summary>
/// DI helpers for wiring an <see cref="ICovclaimdClient"/> into the service container.
/// </summary>
public static class CovclaimdServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="CovclaimdClient"/> as the application's
    /// <see cref="ICovclaimdClient"/>, configures <see cref="CovclaimdOptions"/>,
    /// and wires a typed <see cref="HttpClient"/> via <see cref="IHttpClientFactory"/>.
    /// </summary>
    /// <param name="services">The DI service collection.</param>
    /// <param name="configure">
    /// Setter for <see cref="CovclaimdOptions"/>; at minimum
    /// <see cref="CovclaimdOptions.BaseAddress"/> must be set.
    /// </param>
    public static IServiceCollection AddCovclaimdClient(
        this IServiceCollection services,
        Action<CovclaimdOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        services.AddHttpClient<CovclaimdClient>(CovclaimdOptions.HttpClientName);
        services.AddSingleton<ICovclaimdClient>(sp => sp.GetRequiredService<CovclaimdClient>());
        return services;
    }
}
