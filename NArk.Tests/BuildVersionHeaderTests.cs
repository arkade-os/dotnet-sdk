using NArk.Transport;
using NArk.Transport.RestClient;

namespace NArk.Tests;

[TestFixture]
public class BuildVersionHeaderTests
{
    [Test]
    public void ArkdVersion_TargetBuild_IsExpected()
    {
        Assert.That(ArkdVersion.TargetBuild, Is.EqualTo("0.9.7"));
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
}
