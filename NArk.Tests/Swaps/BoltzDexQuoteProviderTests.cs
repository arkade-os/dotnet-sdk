using System.Net;
using System.Numerics;
using Microsoft.Extensions.Options;
using Nethereum.Web3;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models;
using NArk.Swaps.Evm;
using NArk.Swaps.Evm.Dex;
using NUnit.Framework;

namespace NArk.Tests.Swaps;

/// <summary>
/// Unit tests for <see cref="BoltzDexQuoteProvider"/> — verifies the request/response shape
/// against Boltz's documented <c>/v2/quote/{currency}/in</c>/<c>/encode</c> schema (from
/// boltz-backend's <c>QuoteRouter.ts</c> OpenAPI comments) without hitting the network.
/// </summary>
[TestFixture]
public class BoltzDexQuoteProviderTests
{
    private const string RouterAddress = "0xRouter0000000000000000000000000000000000";

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private static (BoltzDexQuoteProvider provider, List<HttpRequestMessage> requests) BuildProvider(
        Func<HttpRequestMessage, string> responder, decimal slippage = 0.01m)
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new FakeHandler(req =>
        {
            requests.Add(req);
            return JsonResponse(responder(req));
        });

        var boltzClient = new BoltzClient(new HttpClient(handler),
            Options.Create(new BoltzClientOptions { BoltzUrl = "https://example.test/", WebsocketUrl = "wss://example.test/" }));
        var routerClient = new RouterClient(new Web3("http://localhost:1"), RouterAddress);

        return (new BoltzDexQuoteProvider(boltzClient, routerClient, "arbitrum", slippage), requests);
    }

    [Test]
    public async Task GetSwapCallsAsync_HappyPath_AppliesSlippageAndMapsCalls()
    {
        var (provider, requests) = BuildProvider(req => req.RequestUri!.AbsolutePath.EndsWith("/in")
            ? """[{"quote":"1000000","data":{"foo":"bar"}}]"""
            : """{"calls":[{"to":"0xDex00000000000000000000000000000000000000","value":"0","data":"0xdeadbeef"}]}""");

        var result = await provider.GetSwapCallsAsync("0xTokenIn", "0xTokenOut", BigInteger.Parse("500000"));

        // 1% slippage off 1,000,000 -> 990,000.
        Assert.That(result.MinAmountOut, Is.EqualTo(new BigInteger(990_000)));
        Assert.That(result.Calls, Has.Count.EqualTo(1));
        Assert.That(result.Calls[0].Target, Is.EqualTo("0xDex00000000000000000000000000000000000000"));
        Assert.That(result.Calls[0].Value, Is.EqualTo(BigInteger.Zero));
        Assert.That(result.Calls[0].CallData, Is.EqualTo(Convert.FromHexString("deadbeef")));

        // The /in request carries the right query params; /encode gets the Router as recipient.
        Assert.That(requests[0].RequestUri!.Query, Does.Contain("tokenIn=0xTokenIn"));
        Assert.That(requests[0].RequestUri!.Query, Does.Contain("tokenOut=0xTokenOut"));
        Assert.That(requests[0].RequestUri!.Query, Does.Contain("amountIn=500000"));

        var encodeBody = await requests[1].Content!.ReadAsStringAsync();
        Assert.That(encodeBody, Does.Contain(RouterAddress));
        Assert.That(encodeBody, Does.Contain("990000"));
    }

    [Test]
    public void GetSwapCallsAsync_EmptyQuoteList_Throws()
    {
        var (provider, _) = BuildProvider(_ => "[]");

        Assert.That(
            () => provider.GetSwapCallsAsync("0xTokenIn", "0xTokenOut", BigInteger.One),
            Throws.InvalidOperationException);
    }

    [Test]
    public async Task GetSwapCallsAsync_ZeroSlippage_MinAmountOutEqualsQuote()
    {
        var (provider, _) = BuildProvider(req => req.RequestUri!.AbsolutePath.EndsWith("/in")
            ? """[{"quote":"42","data":{}}]"""
            : """{"calls":[]}""", slippage: 0m);

        var result = await provider.GetSwapCallsAsync("0xIn", "0xOut", BigInteger.One);

        Assert.That(result.MinAmountOut, Is.EqualTo(new BigInteger(42)));
    }
}
