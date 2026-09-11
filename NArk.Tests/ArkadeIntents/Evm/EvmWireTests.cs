using System.Text.Json;
using System.Net;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Evm;
using NArk.ArkadeIntents.SolverRegistry;

namespace NArk.Tests.ArkadeIntents.Evm;

public class EvmWireTests
{
    private const string Token = "0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2";

    [Test]
    public void Request_UsesTheCurrentStrictNumericSatsWireShape()
    {
        var request = EvmSendProfile.Request(
            50_000,
            new string('a', 64),
            "0x3c44cdddb6a900fa2b585dd299e03d12fa4293bc",
            "tark1example",
            new string('b', 64),
            Token,
            new string('c', 64));

        var json = JsonSerializer.SerializeToNode(request, RfqProtocol.Json)!.AsObject();

        Assert.Multiple(() =>
        {
            Assert.That(json.Select(p => p.Key), Is.EquivalentTo(
                new[] { "v", "type", "rfq_id", "pair", "amount_side", "amount", "profile" }));
            Assert.That(json["amount"]!.GetValue<long>(), Is.EqualTo(50_000));
            Assert.That(json["amount_side"]!.GetValue<string>(), Is.EqualTo("from"));
            Assert.That(json["pair"]!.GetValue<string>(), Is.EqualTo($"arkade:BTC->ethereum:{Token}"));
            Assert.That(json["profile"]!.AsObject().Select(p => p.Key), Is.EquivalentTo(
                new[] { "payment_hash", "evm_claim_address", "refund_address", "client_refund_pubkey" }));
        });
    }

    [Test]
    public void LegacyPairAdapter_MapsOnlyAnExplicitCanonicalErc20Address()
    {
        var from = AssetIdentifier.Parse("arkade:regtest/slip44:1");
        var token = AssetIdentifier.Parse($"eip155:31337/erc20:{Token}");

        Assert.That(LegacyRfqPairAdapter.FromCanonical(from, token),
            Is.EqualTo($"arkade:BTC->ethereum:{Token}"));
        Assert.That(() => LegacyRfqPairAdapter.FromCanonical(from,
            AssetIdentifier.Parse("eip155:31337/slip44:60")), Throws.ArgumentException);
        Assert.That(() => LegacyRfqPairAdapter.FromCanonical(
            AssetIdentifier.Parse("arkade:regtest/asset:" + new string('a', 68)), token),
            Throws.ArgumentException);
        Assert.That(() => LegacyRfqPairAdapter.FromCanonical(from,
            AssetIdentifier.Parse("eip155:31337/erc20:0x0000000000000000000000000000000000000000")),
            Throws.ArgumentException);
        Assert.That(() => LegacyRfqPairAdapter.FromCanonical(from,
            AssetIdentifier.Parse("eip155:31337/erc20:0xC02AAA39B223FE8D0A0E5C4F27EAD9083C756CC2")),
            Throws.TypeOf<FormatException>());
    }

    [Test]
    public async Task HttpTransportPostsTheStrictRequestAndReadsTheFullWidthTokenAmount()
    {
        var handler = new RecordingHandler();
        var transport = new HttpRfqTransport(new HttpClient(handler), new Uri("https://solver.test/"));
        var request = EvmSendProfile.Request(
            50_000, new string('a', 64), "0x3c44cdddb6a900fa2b585dd299e03d12fa4293bc",
            "tark1example", new string('b', 64), Token, new string('c', 64));

        var quote = await transport.RequestEvmSendQuoteAsync(request);

        Assert.Multiple(() =>
        {
            Assert.That(JsonDocument.Parse(handler.Body!).RootElement.GetProperty("amount").ValueKind,
                Is.EqualTo(JsonValueKind.Number));
            Assert.That(quote.ToAtomicAmount.ToString(), Is.EqualTo("100000000000000000000"));
        });
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("""
                    {"v":1,"type":"rfq_quote","rfq_id":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
                     "pair":"arkade:BTC->ethereum:0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2",
                     "from_amount":50000,"to_amount":"100000000000000000000","solver_pubkey":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"}
                    """),
            };
        }
    }
}
