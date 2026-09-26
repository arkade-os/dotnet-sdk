using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using NArk.ArkadeIntents.Covclaim;
using NBitcoin;

namespace NArk.Tests.ArkadeIntents.Covclaim;

/// <summary>
/// What this wallet puts on the wire when it reveals a claim, and how it fails when the daemon
/// cannot be reached. Field by field, because covclaimd refuses a body whose three parts do not
/// agree — at the one moment the wallet has stopped watching.
/// </summary>
[TestFixture]
public class CovclaimdClientTests
{
    private const string CovclaimdPubKey = "02474c9c627ad19d0ae5c70050703dc62ebbee99885d054623f59f8f79a277904e";
    private const string EmulatorPubKey = "02bb58b5feca505c74edc000d8282fc556e51a1024fc8e7d7e56c6f887c5c8d5f2";

    [Test]
    public async Task Reveal_SendsTheAddress_TheSealedPreimage_AndTheTreeAsHex()
    {
        var handler = new RecordingHandler();
        var client = Client(handler);
        var taptree = new[] { Leaf(0x51), Leaf(0x52) };

        await client.RevealAsync("tark1qexample", new byte[32], [0x6a, 0x51], taptree);

        var body = JsonNode.Parse(handler.LastBody!)!.AsObject();
        Assert.Multiple(() =>
        {
            Assert.That(handler.LastPath, Is.EqualTo("/v1/reveal"));
            Assert.That((string)body["swap_address"]!, Is.EqualTo("tark1qexample"));
            // Sealed preimage: 93 bytes once decoded.
            Assert.That(Convert.FromBase64String((string)body["packet"]!["arkade_script"]!),
                Is.EqualTo(new byte[] { 0x6a, 0x51 }));
            Assert.That(Convert.FromBase64String((string)body["packet"]!["ciphertext"]!).Length,
                Is.EqualTo(93));
            Assert.That((string)body["taptree"]!, Does.Match("^[0-9a-f]+$"));
        });
    }

    [Test]
    public async Task Reveal_SendsNoThirdPacketField()
    {
        var handler = new RecordingHandler();

        await Client(handler).RevealAsync("tark1qexample", new byte[32], [0x51], [Leaf(0x51)]);

        var packet = JsonNode.Parse(handler.LastBody!)!["packet"]!.AsObject();
        Assert.That(packet.Select(p => p.Key), Is.EquivalentTo(new[] { "ciphertext", "arkade_script" }));
    }

    [Test]
    public void Reveal_WithAPreimageOfTheWrongLength_IsRefusedBeforeAnyRequest()
    {
        var handler = new RecordingHandler();

        Assert.Multiple(() =>
        {
            Assert.That(() => Client(handler).RevealAsync("tark1q", new byte[31], [0x51], [Leaf(0x51)]),
                Throws.TypeOf<ArgumentException>());
            Assert.That(handler.Requests, Is.Zero);
        });
    }

    [Test]
    public void Reveal_WithAnEmptyTaptree_IsRefusedBeforeAnyRequest()
    {
        // Refused here, where the caller can still read which argument was empty.
        Assert.That(() => Client(new RecordingHandler()).RevealAsync("tark1q", new byte[32], [0x51], []),
            Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public void ADaemonThatRefuses_SurfacesItsStatus()
    {
        var handler = new RecordingHandler(
            (HttpStatusCode.BadRequest, """{"error":"taptree does not hash to swap_address"}"""));

        var ex = Assert.ThrowsAsync<CovclaimdException>(
            () => Client(handler).RevealAsync("tark1q", new byte[32], [0x51], [Leaf(0x51)]));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.StatusCode, Is.EqualTo(400));
            // The daemon's own words, because "it refused" without them is a support ticket.
            Assert.That(ex.Message, Does.Contain("taptree does not hash"));
        });
    }

    [Test]
    public void AnUnreachableDaemon_IsACovclaimdException_NotATransportOne()
    {
        var handler = new RecordingHandler { Throw = new HttpRequestException("connection refused") };

        Assert.That(() => Client(handler).GetKeysAsync(), Throws.TypeOf<CovclaimdException>());
    }

    [Test]
    public async Task TheDaemonsKeys_AreReReadEveryTime_ByDefault()
    {
        var handler = new RecordingHandler();
        var client = Client(handler);

        await client.GetKeysAsync();
        await client.GetKeysAsync();

        Assert.That(handler.Requests, Is.EqualTo(2));
    }

    [Test]
    public async Task TheDaemonsKeys_AreReadOnce_WhenCachingIsAskedFor()
    {
        var handler = new RecordingHandler();
        var client = Client(handler, cacheKeys: true);

        await client.GetKeysAsync();
        await client.GetKeysAsync();

        Assert.That(handler.Requests, Is.EqualTo(1));
    }

    [Test]
    public void ARemoteDaemonOverPlainHttp_IsRefused()
    {
        Assert.That(() => new CovclaimdClient(
                new HttpClient { BaseAddress = new Uri("http://covclaimd.example") },
                Options.Create(new CovclaimdOptions())),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("https"));
    }

    [Test]
    public void ARemoteDaemonOverHttps_IsAccepted()
    {
        Assert.That(() => new CovclaimdClient(
                new HttpClient { BaseAddress = new Uri("https://covclaimd.example") },
                Options.Create(new CovclaimdOptions())),
            Throws.Nothing);
    }

    [Test]
    public void ARemoteDaemonOverPlainHttp_IsAcceptedOnAnExplicitOptOut()
    {
        // A private network the operator vouches for.
        Assert.That(() => new CovclaimdClient(
                new HttpClient { BaseAddress = new Uri("http://covclaimd.example") },
                Options.Create(new CovclaimdOptions { AllowInsecureHttp = true })),
            Throws.Nothing);
    }

    private static CovclaimdClient Client(RecordingHandler handler, bool cacheKeys = false) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:7271") },
            Options.Create(new CovclaimdOptions { CacheKeys = cacheKeys }));

    private static TapScript Leaf(byte op) => new Script(new[] { op }).ToTapScript(TapLeafVersion.C0);

    private sealed class RecordingHandler((HttpStatusCode Status, string Body)? failure = null)
        : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public string? LastBody { get; private set; }
        public string? LastPath { get; private set; }
        public Exception? Throw { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (Throw is not null) throw Throw;

            LastPath = request.RequestUri!.AbsolutePath;
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            if (LastPath.EndsWith("covclaimd-pubkey", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    covclaimd_pub_key = CovclaimdPubKey,
                    emulator_pub_key = EmulatorPubKey,
                }));
            }

            return failure is { } f ? Json(f.Status, f.Body) : new HttpResponseMessage(HttpStatusCode.OK);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
