using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NArk.Arkade.Covclaim;
using NArk.Core.Contracts;

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
    /// <remarks>
    /// Also registers <see cref="CovclaimdCovenantClaimProvider"/> as the application's
    /// <see cref="ICovenantClaimProvider"/>, which is what lets swap code opt into
    /// covenant claims. Until this is called no provider exists, and every swap path
    /// behaves exactly as it did before covenant claims were available.
    /// </remarks>
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

        // The client is a singleton (it caches the daemon's keys), so it must not hold a
        // handler that the factory intends to rotate — a captured typed client would pin
        // one handler for the lifetime of the app and never pick up DNS changes. Taking a
        // named client from the factory at resolve time, with connection lifetime managed
        // on the handler instead of by rotation, keeps both properties.
        services.AddHttpClient(CovclaimdOptions.HttpClientName)
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            });

        services.TryAddSingleton<ICovclaimdClient>(sp => new CovclaimdClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(CovclaimdOptions.HttpClientName),
            sp.GetRequiredService<IOptions<CovclaimdOptions>>(),
            sp.GetService<ILogger<CovclaimdClient>>()));

        // TryAdd so calling this twice does not stack duplicate providers, which would
        // make swap code pick an arbitrary one.
        services.TryAddSingleton<ICovenantClaimProvider, CovclaimdCovenantClaimProvider>();
        return services;
    }
}
