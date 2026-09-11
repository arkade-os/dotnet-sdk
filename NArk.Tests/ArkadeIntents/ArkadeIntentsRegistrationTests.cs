using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Hosting;

namespace NArk.Tests.ArkadeIntents;

public class ArkadeIntentsRegistrationTests
{
    [TestCase(nameof(ArkadeIntentsOptions.EmulatorPubkeyOverride), "02c6047f9441ed7d6d3045406e95c07cd85c778e4b8cef3ca7abac09b95c709ee5")]
    [TestCase(nameof(ArkadeIntentsOptions.MaxPayAmountSats), 250_000L)]
    [TestCase(nameof(ArkadeIntentsOptions.OnchainClaimConfirmations), 3)]
    public void SuppliedOptions_ReachRegisteredConsumers(string property, object expected)
    {
        var services = new ServiceCollection();
        services.AddArkadeIntentsServices(new ArkadeIntentsOptions
        {
            EmulatorPubkeyOverride = "02c6047f9441ed7d6d3045406e95c07cd85c778e4b8cef3ca7abac09b95c709ee5",
            MaxPayAmountSats = 250_000,
            OnchainClaimConfirmations = 3
        });
        using var provider = services.BuildServiceProvider();
        var actual = provider.GetRequiredService<IOptions<ArkadeIntentsOptions>>().Value;

        Assert.That(typeof(ArkadeIntentsOptions).GetProperty(property)!.GetValue(actual), Is.EqualTo(expected));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Defaults_ReachConsumersWithOrWithoutAnOptionsObject(bool explicitOptions)
    {
        var services = new ServiceCollection();
        services.AddArkadeIntentsServices(explicitOptions ? new ArkadeIntentsOptions() : null);
        using var provider = services.BuildServiceProvider();
        var actual = provider.GetRequiredService<IOptions<ArkadeIntentsOptions>>().Value;

        Assert.Multiple(() =>
        {
            Assert.That(actual.EmulatorPubkeyOverride, Is.Null);
            Assert.That(actual.MaxPayAmountSats, Is.Null);
            Assert.That(actual.OnchainClaimConfirmations, Is.EqualTo(6));
        });
    }

    [Test]
    public void ExplicitDefaults_ReplacePreviouslyConfiguredLimits()
    {
        var services = ConfiguredLimits();
        services.AddArkadeIntentsServices(new ArkadeIntentsOptions());
        using var provider = services.BuildServiceProvider();
        var actual = provider.GetRequiredService<IOptions<ArkadeIntentsOptions>>().Value;

        Assert.Multiple(() =>
        {
            Assert.That(actual.MaxPayAmountSats, Is.Null);
            Assert.That(actual.OnchainClaimConfirmations, Is.EqualTo(6));
        });
    }

    [Test]
    public void NoOptions_PreservesPreviouslyConfiguredLimits()
    {
        var services = ConfiguredLimits();
        services.AddArkadeIntentsServices();
        using var provider = services.BuildServiceProvider();
        var actual = provider.GetRequiredService<IOptions<ArkadeIntentsOptions>>().Value;

        Assert.Multiple(() =>
        {
            Assert.That(actual.MaxPayAmountSats, Is.EqualTo(123_000));
            Assert.That(actual.OnchainClaimConfirmations, Is.EqualTo(2));
        });
    }

    private static IServiceCollection ConfiguredLimits() => new ServiceCollection().Configure<ArkadeIntentsOptions>(options =>
    {
        options.MaxPayAmountSats = 123_000;
        options.OnchainClaimConfirmations = 2;
    });
}
