using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
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
/// Boltz answering the batch status endpoint with 200 OK while omitting some of the
/// requested IDs is not a 404, so it never reaches the not-found fallback. Without an
/// explicit gap check those swaps get no status, never increment the unknown counter,
/// and stay watched for the provider's lifetime.
/// </summary>
[TestFixture]
public class BoltzReconciliationGapTests
{
    [Test]
    public async Task IdsOmittedFromBatchResponse_ArePolledIndividually()
    {
        var requestedPaths = new List<string>();

        var handler = new BoltzTestFixture.StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            requestedPaths.Add(path);

            // The batch answers for swap-a only; swap-b is silently dropped.
            if (path == "/v2/swap/status")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"swap-a":{"status":"invoice.pending"}}""", Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"status":"invoice.pending"}""", Encoding.UTF8, "application/json"),
            };
        });

        var provider = CreateProvider(handler, SwapStorageReturning(Swap("swap-a"), Swap("swap-b")));

        await provider.ReconcileActiveSwaps(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(requestedPaths, Does.Contain("/v2/swap/status"),
                "the batch endpoint should still be used");
            Assert.That(requestedPaths, Does.Contain("/v2/swap/swap-b"),
                "the omitted ID must fall back to an individual poll");
            Assert.That(requestedPaths, Does.Not.Contain("/v2/swap/swap-a"),
                "an ID the batch answered for must not be polled again");
        });
    }

    private static ArkSwap Swap(string swapId) =>
        new(swapId, "wallet-1", ArkSwapType.ReverseSubmarine,
            "lnbc...", 10_000, "script", "address",
            ArkSwapStatus.Pending, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "hash");

    private static ISwapStorage SwapStorageReturning(params ArkSwap[] swaps)
    {
        var storage = Substitute.For<ISwapStorage>();
        storage
            .GetSwaps(Arg.Any<string[]?>(), Arg.Any<string[]?>(), Arg.Any<bool?>(),
                Arg.Any<ArkSwapType[]?>(), Arg.Any<ArkSwapStatus[]?>(), Arg.Any<string[]?>(),
                Arg.Any<string[]?>(), Arg.Any<string[]?>(), Arg.Any<string?>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var swapIds = call.ArgAt<string[]?>(1);
                return Task.FromResult<IReadOnlyCollection<ArkSwap>>(
                    swapIds is null ? swaps : swaps.Where(s => swapIds.Contains(s.SwapId)).ToArray());
            });
        return storage;
    }

    private static BoltzSwapProvider CreateProvider(HttpMessageHandler handler, ISwapStorage swapStorage)
    {
        var options = Options.Create(new BoltzClientOptions
        {
            BoltzUrl = "https://example.test/",
            WebsocketUrl = "wss://example.test/",
        });

        return new BoltzSwapProvider(
            new BoltzClient(new HttpClient(handler), options),
            new BoltzLimitsValidator(new CachedBoltzClient(new HttpClient(handler), options)),
            Substitute.For<IClientTransport>(),
            Substitute.For<IVtxoStorage>(),
            Substitute.For<IWalletProvider>(),
            swapStorage,
            Substitute.For<IContractService>(),
            Substitute.For<IContractStorage>(),
            new AsyncSafetyService(),
            Substitute.For<IIntentStorage>(),
            Substitute.For<IBitcoinBlockchain>());
    }
}
