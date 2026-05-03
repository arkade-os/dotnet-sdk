using System.Net;
using Microsoft.Extensions.Options;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models;

namespace NArk.Tests;

/// <summary>
/// Verifies <see cref="BoltzClient.GetSwapStatusAsync"/> distinguishes
/// "swap unknown to this Boltz instance" (404 + matching body) from generic
/// HTTP failures. The distinction matters because
/// <c>SwapsManagementService.PollSwapState</c> uses <see cref="BoltzSwapNotFoundException"/>
/// to drive a per-swap consecutive-unknown counter, and we must not trip the
/// counter on transient route or proxy 404s.
/// </summary>
[TestFixture]
public class BoltzClientNotFoundTests
{
    private static BoltzClient CreateClient(HttpResponseMessage response)
    {
        var handler = new StubHandler(response);
        var http = new HttpClient(handler);
        var options = Options.Create(new BoltzClientOptions
        {
            BoltzUrl = "https://example.test/",
            WebsocketUrl = "wss://example.test/",
        });
        return new BoltzClient(http, options);
    }

    [Test]
    public void Throws_BoltzSwapNotFoundException_On_404_With_CouldNotFindSwap_Body()
    {
        var resp = new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{\"error\":\"could not find swap with id: zKC8PCx8dddd6Xna\"}"),
        };
        var client = CreateClient(resp);

        var ex = Assert.ThrowsAsync<BoltzSwapNotFoundException>(
            () => client.GetSwapStatusAsync("zKC8PCx8dddd6Xna", CancellationToken.None));
        Assert.That(ex!.SwapId, Is.EqualTo("zKC8PCx8dddd6Xna"));
    }

    [Test]
    public void Throws_HttpRequestException_On_404_With_NonMatching_Body()
    {
        // A 404 from a renamed route or misconfigured proxy must NOT trip the
        // safety net — it propagates as a generic HTTP error so the existing
        // catch-all path logs and continues without tripping the counter.
        var resp = new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("<html><body>404 Not Found</body></html>"),
        };
        var client = CreateClient(resp);

        Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetSwapStatusAsync("anyId", CancellationToken.None));
    }

    [Test]
    public async Task Returns_Status_On_Success()
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"status\":\"swap.created\"}"),
        };
        var client = CreateClient(resp);

        var status = await client.GetSwapStatusAsync("anyId", CancellationToken.None);
        Assert.That(status, Is.Not.Null);
        Assert.That(status!.Status, Is.EqualTo("swap.created"));
    }

    [Test]
    public void Throws_HttpRequestException_On_5xx()
    {
        // 5xx is transient — handled by the generic catch-all in PollSwapState,
        // not by the not-found safety net.
        var resp = new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent(""),
        };
        var client = CreateClient(resp);

        Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetSwapStatusAsync("anyId", CancellationToken.None));
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }
}
