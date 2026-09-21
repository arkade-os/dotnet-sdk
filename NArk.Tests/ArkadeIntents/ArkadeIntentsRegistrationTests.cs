using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Evm;
using NArk.ArkadeIntents.Hosting;
using NArk.ArkadeIntents.Onchain;
using NSubstitute;

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

    /// <summary>
    /// A host that configured neither corridor seam still gets a container that validates.
    /// </summary>
    /// <remarks>
    /// Both clients used to be named unconditionally, so a container built with
    /// <c>ValidateOnBuild</c> — BTCPayServer's is — threw at startup over a corridor the host never
    /// asked for, naming an <c>IEvmSwapRpc</c> it has no way to supply.
    /// </remarks>
    [Test]
    public void NoCorridorSeams_LeaveAValidatableContainer()
    {
        var services = CoreSeams();
        services.AddArkadeIntentsServices();

        Assert.DoesNotThrow(() => services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true }).Dispose());
    }

    [Test]
    public void NoCorridorSeams_LeaveTheirClientsUnregistered()
    {
        var services = CoreSeams();
        services.AddArkadeIntentsServices();

        Assert.Multiple(() =>
        {
            Assert.That(services.Any(d => d.ServiceType == typeof(EvmIntentsClient)), Is.False);
            Assert.That(services.Any(d => d.ServiceType == typeof(OnchainIntentsClient)), Is.False);
        });
    }

    /// <summary>A seam the host did supply still brings its corridor client, resolvable.</summary>
    [Test]
    public void SuppliedSeams_BringTheirCorridorClients()
    {
        var services = CoreSeams();
        services.AddSingleton(Substitute.For<IEvmSwapRpc>());
        services.AddSingleton(Substitute.For<IBitcoinBlockchain>());
        services.AddArkadeIntentsServices();

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true });

        Assert.Multiple(() =>
        {
            Assert.That(provider.GetService<EvmIntentsClient>(), Is.Not.Null);
            Assert.That(provider.GetService<OnchainIntentsClient>(), Is.Not.Null);
        });
    }

    /// <summary>
    /// What any host of the intents package supplies regardless of corridor: the transport it
    /// speaks to the operator over, the wallet and spending seams, and the stores the swap state
    /// lives in. A corridor seam is deliberately not among them — that is what these tests vary.
    /// </summary>
    private static IServiceCollection CoreSeams() => new ServiceCollection()
        .AddSingleton(Substitute.For<IClientTransport>())
        .AddSingleton(Substitute.For<IContractService>())
        .AddSingleton(Substitute.For<ISpendingService>())
        .AddSingleton(Substitute.For<IWalletProvider>())
        .AddSingleton(Substitute.For<IContractStorage>())
        .AddSingleton(Substitute.For<IVtxoStorage>())
        .AddSingleton(Substitute.For<IArkadeIntentStorage>());

    private static IServiceCollection ConfiguredLimits() => new ServiceCollection().Configure<ArkadeIntentsOptions>(options =>
    {
        options.MaxPayAmountSats = 123_000;
        options.OnchainClaimConfirmations = 2;
    });
}
