using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using NArk.Core.Payments;

namespace NArk.Tests;

/// <summary>
/// Pins the public surface of <see cref="LnurlHelper"/> after lifting it out of the WASM sample:
/// detection rules for LNURL / Lightning Address, bech32 decode of <c>lnurl1…</c>, and the
/// callback-fetch wire format (millisat amount, separator handling, error surfacing).
/// </summary>
[TestFixture]
public class LnurlHelperTests
{
    [Test]
    public void IsLnurl_Lightning1Prefix_True()
    {
        Assert.That(LnurlHelper.IsLnurl("LNURL1DP68GURN8GHJ7MRWWPSHJTNRDUH"), Is.True);
    }

    [Test]
    public void IsLnurl_LightningScheme_True()
    {
        Assert.That(LnurlHelper.IsLnurl("lightning:LNURL1DP68GURN8GHJ7"), Is.True);
    }

    [Test]
    public void IsLnurl_LightningAddress_True()
    {
        Assert.That(LnurlHelper.IsLnurl("alice@walletofsatoshi.com"), Is.True);
    }

    [Test]
    public void IsLnurl_BareEmail_True()
    {
        // The test mirrors arkade-os/wallet's lenient detection: anything user@host shaped
        // is treated as a candidate (the well-known fetch is what actually validates it).
        Assert.That(LnurlHelper.IsLnurl("user@example.com"), Is.True);
    }

    [Test]
    public void IsLnurl_OnchainAddress_False()
    {
        Assert.That(LnurlHelper.IsLnurl("bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4"), Is.False);
    }

    [Test]
    public void IsLnurl_AtSignAtStart_False()
    {
        Assert.That(LnurlHelper.IsLnurl("@bad"), Is.False);
    }

    [Test]
    public void DecodeLnurl_RoundTrip_ProducesUrl()
    {
        // Canonical example from the LNURL spec / bech32 LUD-01.
        const string Encoded = "LNURL1DP68GURN8GHJ7UM9WFMXJCM99E3K7MF0V9CXJ0M385EKVCENXC6R2C35XVUKXEFCV5MKVV34X5EKZD3EV56NYD3HXQURZEPEXEJXXEPNXSCRVWFNV9NXZCN9XQ6XYEFHVGCXXCMYXYMNSERXFQ5FNS";
        string url = LnurlHelper.DecodeLnurl(Encoded);
        Assert.That(url, Does.StartWith("https://"));
        Assert.That(Uri.TryCreate(url, UriKind.Absolute, out _), Is.True);
    }

    [Test]
    public async Task FetchInvoiceAsync_AppendsAmount_AndReturnsPr()
    {
        var stub = new StubMessageHandler((req, _) =>
        {
            Assert.That(req.RequestUri!.ToString(), Does.Contain("amount=100000")); // 100 sats → 100_000 msat
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent("{\"pr\":\"lnbc100stub\"}"),
            };
        });
        var helper = new LnurlHelper(new HttpClient(stub));

        string invoice = await helper.FetchInvoiceAsync("https://lnurl.example/cb", amountSats: 100);

        Assert.That(invoice, Is.EqualTo("lnbc100stub"));
    }

    [Test]
    public async Task FetchInvoiceAsync_CallbackAlreadyHasQuery_UsesAmpersand()
    {
        var stub = new StubMessageHandler((req, _) =>
        {
            // ?session=abc preserved + & separator + amount appended.
            Assert.That(req.RequestUri!.Query, Does.Contain("session=abc"));
            Assert.That(req.RequestUri!.Query, Does.Contain("&amount=1000"));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent("{\"pr\":\"lnbc1stub\"}"),
            };
        });
        var helper = new LnurlHelper(new HttpClient(stub));

        await helper.FetchInvoiceAsync("https://lnurl.example/cb?session=abc", amountSats: 1);
    }

    [Test]
    public void FetchInvoiceAsync_ServerReasonField_Throws()
    {
        var stub = new StubMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("{\"reason\":\"amount too small\"}"),
        });
        var helper = new LnurlHelper(new HttpClient(stub));

        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            () => helper.FetchInvoiceAsync("https://lnurl.example/cb", 1));
        Assert.That(ex!.Message, Does.Contain("amount too small"));
    }

    private static StringContent JsonContent(string json)
    {
        var c = new StringContent(json);
        c.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return c;
    }

    private sealed class StubMessageHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request, cancellationToken));
    }
}
