using NArk.Transport;
using NArk.Transport.RestClient;

namespace NArk.Tests;

[TestFixture]
public class BuildVersionHeaderTests
{
    [Test]
    public void ArkdVersion_TargetBuild_IsExpected()
    {
        Assert.That(ArkdVersion.TargetBuild, Is.EqualTo("0.9.9"));
    }

    [Test]
    public void InjectHeader_HttpClient_AddsXBuildVersionHeader()
    {
        var http = new HttpClient();

        http.InjectHeader();

        Assert.That(
            http.DefaultRequestHeaders.GetValues("X-Build-Version"),
            Contains.Item(ArkdVersion.TargetBuild));
    }

    [Test]
    public void InjectHeader_HttpClient_IsIdempotent()
    {
        var http = new HttpClient();

        http.InjectHeader();
        http.InjectHeader();

        Assert.That(http.DefaultRequestHeaders.GetValues("X-Build-Version").ToList(), Has.Count.EqualTo(1));
    }

    [Test]
    public void RestClientTransport_Constructor_InjectsHeaderOnProvidedHttpClient()
    {
        var http = new HttpClient { BaseAddress = new Uri("http://localhost:9999") };

        _ = new RestClientTransport(http);

        Assert.That(
            http.DefaultRequestHeaders.GetValues("X-Build-Version"),
            Contains.Item(ArkdVersion.TargetBuild));
    }

    /// <summary>
    /// The HttpClient constructor does not insert BuildVersionHandler into the pipeline —
    /// X-Digest is never sent and DIGEST_MISMATCH / BUILD_VERSION_TOO_OLD are not detected.
    /// This test documents that known limitation so the gap is visible and intentional.
    /// </summary>
    [Test]
    public async Task RestClientTransport_HttpClientConstructor_DoesNotInjectXDigest()
    {
        HttpRequestMessage? captured = null;
        var stub = new StubHandler(req => { captured = req; return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") }; });
        var http = new HttpClient(stub) { BaseAddress = new Uri("http://localhost:9999") };

        var transport = new RestClientTransport(http);
        // Simulate having a digest by calling nothing — the holder is isolated and never populated.
        // Even if a digest were somehow set, it would not reach the request.
        try { await http.GetAsync("/v1/info"); } catch { /* ignored */ }

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.Headers.Contains(ArkdVersion.DigestHeaderName), Is.False,
            "HttpClient constructor does not insert BuildVersionHandler — X-Digest is the caller's responsibility.");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
