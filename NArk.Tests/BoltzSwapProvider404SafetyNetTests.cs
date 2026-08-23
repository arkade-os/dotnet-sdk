using System.Net;
using Microsoft.Extensions.Options;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.CoinSelector;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Safety.AsyncKeyedLock;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models;
using NArk.Swaps.Models;
using NSubstitute;

namespace NArk.Tests;

/// <summary>
/// Verifies the 404 safety-net in <see cref="BoltzSwapProvider.PollSwapState"/>.
/// When Boltz consistently reports a swap as "not found" (HTTP 404 + matching body),
/// the provider must transition the swap to <see cref="ArkSwapStatus.Failed"/> after
/// <c>UnknownToProviderThreshold</c> (= 10) consecutive failures, then fire
/// <see cref="BoltzSwapProvider.SwapStatusChanged"/> and stop monitoring the swap.
/// </summary>
[TestFixture]
public class BoltzSwapProvider404SafetyNetTests
{
    private const int Threshold = 10; // mirrors BoltzSwapProvider.UnknownToProviderThreshold

    private static BoltzSwapProvider CreateProvider(
        HttpMessageHandler boltzHandler,
        ISwapStorage swapStorage,
        ISafetyService safetyService)
    {
        var options = Options.Create(new BoltzClientOptions
        {
            BoltzUrl = "https://example.test/",
            WebsocketUrl = "wss://example.test/",
        });

        var boltzClient = new BoltzClient(new HttpClient(boltzHandler), options);
        var cachedClient = new CachedBoltzClient(new HttpClient(boltzHandler), options);
        var limitsValidator = new BoltzLimitsValidator(cachedClient);

        var clientTransport = Substitute.For<IClientTransport>();
        var vtxoStorage = Substitute.For<IVtxoStorage>();
        var walletProvider = Substitute.For<IWalletProvider>();
        var contractService = Substitute.For<IContractService>();
        var contractStorage = Substitute.For<IContractStorage>();
        var chainTime = Substitute.For<IBitcoinBlockchain>();
        var intentStorage = Substitute.For<IIntentStorage>();
        
        return new BoltzSwapProvider(boltzClient, limitsValidator, clientTransport,
            vtxoStorage, walletProvider, swapStorage, contractService, contractStorage,
            safetyService, intentStorage, chainTime);
    }

    [Test]
    public async Task TripsAfterThreshold_SwapMovesToFailed_AndEventFires()
    {
        const string swapId = "test-swap-abc123";
        const string walletId = "wallet-1";

        var testSwap = new ArkSwap(swapId, walletId, ArkSwapType.ReverseSubmarine,
            "lnbc...", 10_000, "script", "address",
            ArkSwapStatus.Pending, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "hash");

        // Swap storage returns the test swap for any query and captures the saved swap
        var swapStorage = Substitute.For<ISwapStorage>();
        swapStorage
            .GetSwaps(Arg.Any<string[]?>(), Arg.Any<string[]?>(), Arg.Any<bool?>(),
                Arg.Any<ArkSwapType[]?>(), Arg.Any<ArkSwapStatus[]?>(), Arg.Any<string[]?>(),
                Arg.Any<string[]?>(), Arg.Any<string[]?>(), Arg.Any<string?>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<ArkSwap>>([testSwap]));

        ArkSwap? savedSwap = null;
        swapStorage
            .SaveSwap(Arg.Any<string>(), Arg.Do<ArkSwap>(s => savedSwap = s), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var safetyService = new AsyncSafetyService();
        var handler = new AlwaysNotFoundHandler(swapId);
        var provider = CreateProvider(handler, swapStorage, safetyService);

        SwapStatusChangedEvent? firedEvent = null;
        provider.SwapStatusChanged += (_, e) => firedEvent = e;

        // Drive PollSwapState exactly Threshold times (each call increments the counter;
        // the last call trips the safety net)
        for (var i = 0; i < Threshold; i++)
            await provider.PollSwapState([swapId], CancellationToken.None);

        Assert.That(savedSwap, Is.Not.Null, "SaveSwap must have been called with the failed swap");
        Assert.That(savedSwap!.Status, Is.EqualTo(ArkSwapStatus.Failed));
        Assert.That(savedSwap.FailReason, Is.Not.Null.And.Not.Empty);

        Assert.That(firedEvent, Is.Not.Null, "SwapStatusChanged must fire when safety net trips");
        Assert.That(firedEvent!.SwapId, Is.EqualTo(swapId));
        Assert.That(firedEvent.NewStatus, Is.EqualTo(ArkSwapStatus.Failed));
    }

    [Test]
    public async Task DoesNotTripBelowThreshold_SwapStaysPending()
    {
        const string swapId = "test-swap-below";
        const string walletId = "wallet-2";

        var testSwap = new ArkSwap(swapId, walletId, ArkSwapType.Submarine,
            "lnbc...", 5_000, "script", "address",
            ArkSwapStatus.Pending, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "hash");

        var swapStorage = Substitute.For<ISwapStorage>();
        swapStorage
            .GetSwaps(Arg.Any<string[]?>(), Arg.Any<string[]?>(), Arg.Any<bool?>(),
                Arg.Any<ArkSwapType[]?>(), Arg.Any<ArkSwapStatus[]?>(), Arg.Any<string[]?>(),
                Arg.Any<string[]?>(), Arg.Any<string[]?>(), Arg.Any<string?>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<ArkSwap>>([testSwap]));

        var safetyService = new AsyncSafetyService();
        var handler = new AlwaysNotFoundHandler(swapId);
        var provider = CreateProvider(handler, swapStorage, safetyService);

        SwapStatusChangedEvent? firedEvent = null;
        provider.SwapStatusChanged += (_, e) => firedEvent = e;

        // One below threshold — safety net must NOT trip
        for (var i = 0; i < Threshold - 1; i++)
            await provider.PollSwapState([swapId], CancellationToken.None);

        Assert.That(firedEvent, Is.Null, "SwapStatusChanged must not fire below the threshold");
        await swapStorage.DidNotReceive().SaveSwap(
            Arg.Any<string>(), Arg.Any<ArkSwap>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SuccessfulPollResetsCounter_SubsequentThresholdIsIndependent()
    {
        const string swapId = "test-swap-reset";
        const string walletId = "wallet-3";

        var testSwap = new ArkSwap(swapId, walletId, ArkSwapType.ReverseSubmarine,
            "lnbc...", 8_000, "script", "address",
            ArkSwapStatus.Pending, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "hash");

        var swapStorage = Substitute.For<ISwapStorage>();
        swapStorage
            .GetSwaps(Arg.Any<string[]?>(), Arg.Any<string[]?>(), Arg.Any<bool?>(),
                Arg.Any<ArkSwapType[]?>(), Arg.Any<ArkSwapStatus[]?>(), Arg.Any<string[]?>(),
                Arg.Any<string[]?>(), Arg.Any<string[]?>(), Arg.Any<string?>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<ArkSwap>>([testSwap]));
        swapStorage
            .SaveSwap(Arg.Any<string>(), Arg.Any<ArkSwap>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var safetyService = new AsyncSafetyService();

        // Interleaved handler: first (Threshold-1) requests → 404, then one success, then 404s again
        var callCount = 0;
        var handler = new FakeHandler(req =>
        {
            callCount++;
            if (callCount < Threshold || callCount == Threshold)
            {
                // 404 for first Threshold-1 polls, then one success
                return callCount < Threshold
                    ? NotFoundResponse(swapId)
                    : OkResponse("swap.created");
            }
            // Back to 404 after the successful poll
            return NotFoundResponse(swapId);
        });

        var provider = CreateProvider(handler, swapStorage, safetyService);
        SwapStatusChangedEvent? firedEvent = null;
        provider.SwapStatusChanged += (_, e) => firedEvent = e;

        // Threshold-1 consecutive 404s → counter is at Threshold-1
        for (var i = 0; i < Threshold - 1; i++)
            await provider.PollSwapState([swapId], CancellationToken.None);

        // One successful poll must reset the counter
        await provider.PollSwapState([swapId], CancellationToken.None);

        // One more 404 — with counter reset, a single 404 must NOT trip the safety net
        await provider.PollSwapState([swapId], CancellationToken.None);

        Assert.That(firedEvent, Is.Null,
            "Safety net must not trip after counter was reset by a successful poll");
    }

    private static HttpResponseMessage NotFoundResponse(string swapId) =>
        new(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"{{\"error\":\"could not find swap with id: {swapId}\"}}"),
        };

    private static HttpResponseMessage OkResponse(string status) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent($"{{\"status\":\"{status}\"}}"),
        };

    private sealed class AlwaysNotFoundHandler(string swapId) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(NotFoundResponse(swapId));
    }

    private sealed class FakeHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
