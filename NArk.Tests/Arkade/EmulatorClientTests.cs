using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NArk.Arkade.Emulator;

namespace NArk.Tests.Arkade;

/// <summary>
/// Drives the real <see cref="EmulatorClient"/> over a stub
/// <see cref="HttpMessageHandler"/> to pin the REST wire contract against
/// <c>arkade-os/emulator</c> (api-spec/protobuf/emulator/v1/service.proto):
/// the <c>/v1/onchain-tx</c> endpoint and the <c>deprecatedSignerPubkeys</c>
/// field on <c>/v1/info</c>.
/// </summary>
[TestFixture]
public class EmulatorClientTests
{
    [Test]
    public async Task SubmitOnchainTx_PostsTxField_AndReturnsSignedTx()
    {
        var handler = new StubHandler("{\"signedTx\":\"U0lHTkVE\"}");
        var signed = await Client(handler).SubmitOnchainTxAsync("UkFX");

        Assert.Multiple(() =>
        {
            Assert.That(signed, Is.EqualTo("U0lHTkVE"));
            Assert.That(handler.LastRequest!.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(handler.LastRequest!.RequestUri!.AbsolutePath, Is.EqualTo("/v1/onchain-tx"));
        });

        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.That(body.RootElement.GetProperty("tx").GetString(), Is.EqualTo("UkFX"));
        Assert.That(body.RootElement.EnumerateObject().Count(), Is.EqualTo(1), "only the tx field");
    }

    [Test]
    public void SubmitOnchainTx_MissingSignedTx_Throws()
    {
        Assert.That(async () => await Client(new StubHandler("{}")).SubmitOnchainTxAsync("UkFX"),
            Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public async Task GetInfo_ParsesDeprecatedSignerPubkeys()
    {
        var handler = new StubHandler(
            "{\"version\":\"0.0.1\",\"signerPubkey\":\"02aa\",\"deprecatedSignerPubkeys\":[\"03bb\",\"03cc\"]}");
        var info = await Client(handler).GetInfoAsync();

        Assert.Multiple(() =>
        {
            Assert.That(info.Version, Is.EqualTo("0.0.1"));
            Assert.That(info.SignerPubkey, Is.EqualTo("02aa"));
            Assert.That(info.DeprecatedSignerPubkeys, Is.EqualTo(new[] { "03bb", "03cc" }));
        });
    }

    [Test]
    public async Task GetInfo_NoDeprecatedField_YieldsEmptyList()
    {
        var info = await Client(new StubHandler("{\"signerPubkey\":\"02aa\"}")).GetInfoAsync();
        Assert.That(info.DeprecatedSignerPubkeys, Is.Not.Null.And.Empty);
    }

    private static EmulatorClient Client(StubHandler handler) =>
        new(new HttpClient(handler), Options.Create(new EmulatorClientOptions { ServerUrl = "http://emulator" }));

    private sealed class StubHandler(string responseJson, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }
}
