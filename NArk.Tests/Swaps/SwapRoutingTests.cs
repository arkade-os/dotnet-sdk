using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.CoinSelector;
using NArk.Core.Events;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Services;
using NSubstitute;

namespace NArk.Tests;

[TestFixture]
public class SwapRoutingTests
{
    // Well-known routes for testing
    private static readonly SwapRoute ArkToLn = new(SwapAsset.ArkBtc, SwapAsset.BtcLightning);
    private static readonly SwapRoute LnToArk = new(SwapAsset.BtcLightning, SwapAsset.ArkBtc);
    private static readonly SwapRoute ArkToOnchain = new(SwapAsset.ArkBtc, SwapAsset.BtcOnchain);
    private static readonly SwapRoute OnchainToArk = new(SwapAsset.BtcOnchain, SwapAsset.ArkBtc);

    private SwapsManagementService _sut = null!;
    private ISwapProvider _providerA = null!;
    private ISwapProvider _providerB = null!;

    [SetUp]
    public void SetUp()
    {
        // Provider A supports Ark<->Lightning
        _providerA = CreateMockProvider("provider-a", "Provider A", ArkToLn, LnToArk);

        // Provider B supports Ark<->Onchain
        _providerB = CreateMockProvider("provider-b", "Provider B", ArkToOnchain, OnchainToArk);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_sut != null)
            await _sut.DisposeAsync();
        if (_providerA != null)
            await _providerA.DisposeAsync();
        if (_providerB != null)
            await _providerB.DisposeAsync();
    }

    // ─── Test 1: GetAvailableRoutesAsync aggregates routes ─────────

    [Test]
    public async Task GetAvailableRoutesAsync_AggregatesRoutesFromAllProviders()
    {
        _sut = BuildService(_providerA, _providerB);

        var routes = await _sut.GetAvailableRoutesAsync(CancellationToken.None);

        Assert.That(routes, Has.Count.EqualTo(4));
        Assert.That(routes, Does.Contain(ArkToLn));
        Assert.That(routes, Does.Contain(LnToArk));
        Assert.That(routes, Does.Contain(ArkToOnchain));
        Assert.That(routes, Does.Contain(OnchainToArk));
    }

    [Test]
    public async Task GetAvailableRoutesAsync_DeduplicatesOverlappingRoutes()
    {
        // Both providers support the same route
        var providerC = CreateMockProvider("provider-c", "Provider C", ArkToLn);
        _sut = BuildService(_providerA, providerC);

        var routes = await _sut.GetAvailableRoutesAsync(CancellationToken.None);

        // ArkToLn should appear only once due to Distinct()
        Assert.That(routes.Count(r => r == ArkToLn), Is.EqualTo(1));
        // Total: ArkToLn (deduped) + LnToArk = 2
        Assert.That(routes, Has.Count.EqualTo(2));
    }

    // ─── Test 2: ResolveProvider selects by route ──────────────────

    [Test]
    public void ResolveProvider_SelectsCorrectProviderByRoute()
    {
        _sut = BuildService(_providerA, _providerB);

        var resolved = _sut.ResolveProvider(ArkToLn);
        Assert.That(resolved.ProviderId, Is.EqualTo("provider-a"));

        var resolved2 = _sut.ResolveProvider(ArkToOnchain);
        Assert.That(resolved2.ProviderId, Is.EqualTo("provider-b"));
    }

    // ─── Test 3: ResolveProvider respects PreferredProviderId ──────

    [Test]
    public void ResolveProvider_RespectsPreferredProviderId()
    {
        // Both providers support ArkToLn
        var providerC = CreateMockProvider("provider-c", "Provider C", ArkToLn, LnToArk);
        _sut = BuildService(_providerA, providerC);

        // Without preference, first provider wins
        var defaultResolved = _sut.ResolveProvider(ArkToLn);
        Assert.That(defaultResolved.ProviderId, Is.EqualTo("provider-a"));

        // With preference for provider-c
        var preferred = _sut.ResolveProvider(ArkToLn, preferredProviderId: "provider-c");
        Assert.That(preferred.ProviderId, Is.EqualTo("provider-c"));
    }

    [Test]
    public void ResolveProvider_FallsBackWhenPreferredDoesNotSupportRoute()
    {
        _sut = BuildService(_providerA, _providerB);

        // Prefer provider-b, but it does not support ArkToLn -> falls back to provider-a
        var resolved = _sut.ResolveProvider(ArkToLn, preferredProviderId: "provider-b");
        Assert.That(resolved.ProviderId, Is.EqualTo("provider-a"));
    }

    // ─── Test 4: ResolveProvider throws on unsupported route ───────

    [Test]
    public void ResolveProvider_ThrowsWhenNoProviderSupportsRoute()
    {
        _sut = BuildService(_providerA, _providerB);

        var unsupportedRoute = new SwapRoute(
            SwapAsset.Erc20(SwapNetwork.EvmEthereum, "0xdead"),
            SwapAsset.ArkBtc);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _sut.ResolveProvider(unsupportedRoute));

        Assert.That(ex!.Message, Does.Contain("No provider supports route"));
    }

    // ─── Test 5: GetLimitsAsync delegates to correct provider ──────

    [Test]
    public async Task GetLimitsAsync_DelegatesToCorrectProvider()
    {
        var expectedLimits = new SwapLimits
        {
            Route = ArkToLn,
            MinAmount = 1000,
            MaxAmount = 1_000_000,
            FeePercentage = 0.5m,
            MinerFee = 250
        };

        _providerA.GetLimitsAsync(ArkToLn, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(expectedLimits));

        _sut = BuildService(_providerA, _providerB);

        var limits = await _sut.GetLimitsAsync(ArkToLn, CancellationToken.None);

        Assert.That(limits, Is.EqualTo(expectedLimits));
        await _providerA.Received(1).GetLimitsAsync(ArkToLn, Arg.Any<CancellationToken>());
        await _providerB.DidNotReceive().GetLimitsAsync(Arg.Any<SwapRoute>(), Arg.Any<CancellationToken>());
    }

    // ─── Test 6: GetQuoteAsync delegates to correct provider ───────

    [Test]
    public async Task GetQuoteAsync_DelegatesToCorrectProvider()
    {
        var expectedQuote = new SwapQuote
        {
            Route = ArkToOnchain,
            SourceAmount = 100_000,
            DestinationAmount = 99_000,
            TotalFees = 1_000,
            ExchangeRate = 1.0m
        };

        _providerB.GetQuoteAsync(ArkToOnchain, 100_000, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(expectedQuote));

        _sut = BuildService(_providerA, _providerB);

        var quote = await _sut.GetQuoteAsync(ArkToOnchain, 100_000, CancellationToken.None);

        Assert.That(quote, Is.EqualTo(expectedQuote));
        await _providerB.Received(1).GetQuoteAsync(ArkToOnchain, 100_000, Arg.Any<CancellationToken>());
        await _providerA.DidNotReceive().GetQuoteAsync(Arg.Any<SwapRoute>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    // ─── Test: Providers property exposes all registered providers ──

    [Test]
    public void Providers_ExposesAllRegisteredProviders()
    {
        _sut = BuildService(_providerA, _providerB);

        Assert.That(_sut.Providers, Has.Count.EqualTo(2));
        Assert.That(_sut.Providers.Select(p => p.ProviderId),
            Is.EquivalentTo(new[] { "provider-a", "provider-b" }));
    }

    // ─── Helpers ───────────────────────────────────────────────────

    private static ISwapProvider CreateMockProvider(string id, string displayName, params SwapRoute[] routes)
    {
        var routeSet = routes.ToHashSet();
        var provider = Substitute.For<ISwapProvider>();
        provider.ProviderId.Returns(id);
        provider.DisplayName.Returns(displayName);
        provider.SupportsRoute(Arg.Any<SwapRoute>())
            .Returns(callInfo => routeSet.Contains(callInfo.Arg<SwapRoute>()));
        provider.GetAvailableRoutesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<SwapRoute>>(routeSet.ToList().AsReadOnly()));
        return provider;
    }

    private SwapsManagementService BuildService(params ISwapProvider[] providers)
    {
        // Create SpendingService with all mocked dependencies.
        // Routing tests never invoke SpendingService, so these are just placeholders.
        var spendingService = new SpendingService(
            vtxoStorage: Substitute.For<IVtxoStorage>(),
            contractStorage: Substitute.For<IContractStorage>(),
            walletProvider: Substitute.For<IWalletProvider>(),
            coinService: Substitute.For<ICoinService>(),
            paymentService: Substitute.For<IContractService>(),
            transport: Substitute.For<IClientTransport>(),
            coinSelector: Substitute.For<ICoinSelector>(),
            safetyService: Substitute.For<ISafetyService>(),
            intentStorage: Substitute.For<IIntentStorage>());

        return new SwapsManagementService(
            providers: providers,
            spendingService: spendingService,
            clientTransport: Substitute.For<IClientTransport>(),
            vtxoStorage: Substitute.For<IVtxoStorage>(),
            walletProvider: Substitute.For<IWalletProvider>(),
            swapsStorage: Substitute.For<ISwapStorage>(),
            contractService: Substitute.For<IContractService>(),
            contractStorage: Substitute.For<IContractStorage>(),
            safetyService: Substitute.For<ISafetyService>(),
            intentStorage: Substitute.For<IIntentStorage>(),
            chainTimeProvider: Substitute.For<IChainTimeProvider>());
    }
}
