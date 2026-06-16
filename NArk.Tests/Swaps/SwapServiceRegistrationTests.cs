using Microsoft.Extensions.DependencyInjection;
using NArk.Core.Models.Options;
using NArk.Hosting;
using NArk.Swaps.Boltz.Client;

namespace NArk.Tests;

/// <summary>
/// Pins the contract that the swap-services DI helpers are self-contained for what they own.
/// A previous version of the SDK split <see cref="BoltzClient"/> registration out into the
/// <c>ArkApplicationBuilder.EnableSwaps</c> helper, which silently broke direct-DI consumers
/// with an opaque <c>Unable to resolve service for type 'BoltzClient'</c> when the swap
/// provider was first resolved.
/// </summary>
[TestFixture]
public class SwapServiceRegistrationTests
{
    /// <summary>
    /// <see cref="SwapServiceCollectionExtensions.AddBoltzProvider"/> alone must be enough to
    /// resolve <see cref="BoltzClient"/> — no manual <c>AddHttpClient&lt;BoltzClient&gt;</c>
    /// call required from the consumer.
    /// </summary>
    [Test]
    public void AddBoltzProvider_RegistersBoltzClient()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new ArkNetworkConfig("https://example/ark", "", "https://boltz.example", ""));
        services.AddBoltzProvider();

        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<BoltzClient>();
        Assert.That(client, Is.Not.Null);
    }

    /// <summary>
    /// <see cref="SwapServiceCollectionExtensions.AddArkSwapServices"/> transitively registers
    /// <see cref="BoltzClient"/> too (it calls <c>AddBoltzProvider</c> internally).
    /// </summary>
    [Test]
    public void AddArkSwapServices_RegistersBoltzClient()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new ArkNetworkConfig("https://example/ark", "", "https://boltz.example", ""));
        services.AddArkSwapServices();

        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<BoltzClient>();
        Assert.That(client, Is.Not.Null);
    }
}
