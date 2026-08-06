using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Lightning;

namespace NArk.Tests.ArkadeIntents.Lightning;

/// <summary>
/// The RFQ wire contract as a maker sees it: what we put on the wire, and what we accept back.
/// </summary>
/// <remarks>
/// Two asymmetric rules from the spec drive most of these. Requests are <b>strict</b> — a solver
/// answers an unknown field with <c>unsupported_payload</c>, so an accidental extra property here
/// would break every swap against a conforming solver. Responses are <b>tolerant</b> — refusing to
/// parse a quote carrying a field we have not heard of would break the moment a solver extends its
/// responses, which the spec explicitly permits without a version bump.
/// </remarks>
[TestFixture]
public class HttpRfqTransportTests
{
    private const string RfqId = "9f2c00000000000000000000000000000000000000000000000000000000a1b2";
    private static readonly Uri Solver = new("http://solver.test:8787");

    [Test]
    public void SendRequest_SerializesExactlyTheStrictShape()
    {
        var request = LightningSendProfile.Request("lnbcrt1...", "ark1qexample", RfqId);

        var json = JsonNode.Parse(JsonSerializer.Serialize(request, RfqProtocol.Json))!.AsObject();

        Assert.Multiple(() =>
        {
            Assert.That(json["v"]!.GetValue<int>(), Is.EqualTo(1));
            Assert.That(json["type"]!.GetValue<string>(), Is.EqualTo("rfq_request"));
            Assert.That(json["rfq_id"]!.GetValue<string>(), Is.EqualTo(RfqId));
            Assert.That(json["pair"]!.GetValue<string>(), Is.EqualTo(LightningSendProfile.Pair));
            // A BOLT11 profile is exact-out: the invoice fixes what the solver must pay.
            Assert.That(json["amount_side"]!.GetValue<string>(), Is.EqualTo("to"));
            // Omitted, not null: sending an amount that disagrees with the invoice is a refusal,
            // and a null would be an unknown-shaped field to a strict parser.
            Assert.That(json.ContainsKey("amount"), Is.False);
            Assert.That(json["profile"]!.AsObject().Count, Is.EqualTo(2));
            Assert.That(json["profile"]!["invoice"]!.GetValue<string>(), Is.EqualTo("lnbcrt1..."));
            Assert.That(json["profile"]!["refund_address"]!.GetValue<string>(), Is.EqualTo("ark1qexample"));
            Assert.That(json.Count, Is.EqualTo(6), "an extra top-level field is unsupported_payload");
        });
    }

    [Test]
    public void NewRfqId_IsThirtyTwoUnpredictableBytesAsLowercaseHex()
    {
        var first = RfqProtocol.NewRfqId();

        Assert.That(first, Has.Length.EqualTo(64));
        Assert.That(first, Does.Match("^[0-9a-f]{64}$"));
        Assert.That(first, Is.Not.EqualTo(RfqProtocol.NewRfqId()));
    }

    [Test]
    public async Task RequestQuote_ReturnsTheBindingFields()
    {
        var transport = TransportReturning(HttpStatusCode.Created, Quote());

        var quote = await transport.RequestQuoteAsync<LightningSendRequestProfile, LightningSendQuoteProfile>(LightningSendProfile.Request("lnbcrt1...", "ark1qexample", RfqId));

        Assert.Multiple(() =>
        {
            Assert.That(quote.SolverPubkey, Is.EqualTo("ae75000000000000000000000000000000000000000000000000000000000009"));
            Assert.That(quote.RefundLocktime, Is.EqualTo(1800605184));
            Assert.That(quote.ValidUntil, Is.EqualTo(1800000900));
            Assert.That(quote.FromAmount, Is.EqualTo(50000));
            Assert.That(quote.ToAmount, Is.EqualTo(50000));
            Assert.That(quote.Profile!.LockupAddress, Is.EqualTo("ark1qlockup"));
        });
    }

    [Test]
    public async Task RequestQuote_IgnoresFieldsItHasNotHeardOf()
    {
        // Solvers may extend responses without a version bump; refusing to parse would break the
        // client the day one does.
        var quote = Quote();
        quote["some_future_field"] = "whatever";
        quote["profile"]!["another_one"] = 42;
        var transport = TransportReturning(HttpStatusCode.Created, quote);

        var parsed = await transport.RequestQuoteAsync<LightningSendRequestProfile, LightningSendQuoteProfile>(LightningSendProfile.Request("lnbcrt1...", "ark1qexample", RfqId));

        Assert.That(parsed.SolverPubkey, Is.Not.Null);
    }

    [TestCase(HttpStatusCode.UnprocessableEntity)]
    [TestCase(HttpStatusCode.BadRequest)]
    public void RequestQuote_ThrowsOnARefusal_WhateverTheStatusCode(HttpStatusCode status)
    {
        // The payload is the contract; the HTTP status is envelope. A refusal is a refusal.
        var transport = TransportReturning(status, new JsonObject
        {
            ["v"] = 1, ["type"] = "rfq_refusal", ["rfq_id"] = RfqId, ["reason"] = "exposure_cap",
        });

        var ex = Assert.ThrowsAsync<RfqRefusedException>(() =>
            transport.RequestQuoteAsync<LightningSendRequestProfile, LightningSendQuoteProfile>(LightningSendProfile.Request("lnbcrt1...", "ark1qexample", RfqId)));

        Assert.That(ex!.Reason, Is.EqualTo(RfqRefusalReason.ExposureCap));
        Assert.That(ex.RfqId, Is.EqualTo(RfqId));
    }

    [Test]
    public void RequestQuote_DegradesAReasonOutsideTheClosedSet()
    {
        // The spec: treat an unknown reason as a generic decline, infer no retry semantics.
        var transport = TransportReturning(HttpStatusCode.UnprocessableEntity, new JsonObject
        {
            ["v"] = 1, ["type"] = "rfq_refusal", ["rfq_id"] = RfqId, ["reason"] = "solver_had_a_bad_day",
        });

        var ex = Assert.ThrowsAsync<RfqRefusedException>(() =>
            transport.RequestQuoteAsync<LightningSendRequestProfile, LightningSendQuoteProfile>(LightningSendProfile.Request("lnbcrt1...", "ark1qexample", RfqId)));

        Assert.That(ex!.Reason, Is.EqualTo(RfqRefusalReason.Unknown));
    }

    [Test]
    public void RequestQuote_RefusesAQuoteForADifferentNegotiation()
    {
        // A stale or misrouted reply must never reach the derivation step.
        var quote = Quote();
        quote["rfq_id"] = "dead00000000000000000000000000000000000000000000000000000000beef";
        var transport = TransportReturning(HttpStatusCode.Created, quote);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.RequestQuoteAsync<LightningSendRequestProfile, LightningSendQuoteProfile>(LightningSendProfile.Request("lnbcrt1...", "ark1qexample", RfqId)));

        Assert.That(ex!.Message, Does.Contain(RfqId));
    }

    [Test]
    public void RequestQuote_ThrowsOnAReplyThatIsNeitherQuoteNorRefusal()
    {
        var transport = TransportReturning(HttpStatusCode.OK, new JsonObject { ["v"] = 1, ["type"] = "not_found" });

        Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.RequestQuoteAsync<LightningSendRequestProfile, LightningSendQuoteProfile>(LightningSendProfile.Request("lnbcrt1...", "ark1qexample", RfqId)));
    }

    [Test]
    public async Task GetStatus_ReturnsNullForAnUnknownNegotiation()
    {
        var transport = TransportReturning(HttpStatusCode.NotFound,
            new JsonObject { ["v"] = 1, ["type"] = "not_found" });

        Assert.That(await transport.GetStatusAsync<LightningSendStatusProfile>(RfqId), Is.Null);
    }

    [Test]
    public async Task GetStatus_ReadsTheStateAndTheSettledPreimage()
    {
        var transport = TransportReturning(HttpStatusCode.OK, new JsonObject
        {
            ["v"] = 1,
            ["type"] = "rfq_status",
            ["rfq_id"] = RfqId,
            ["state"] = "settled",
            ["updated_at"] = 1800003600,
            ["profile"] = new JsonObject { ["preimage"] = "00ff", ["payment_hash"] = "abcd" },
        });

        var status = await transport.GetStatusAsync<LightningSendStatusProfile>(RfqId);

        Assert.That(status!.State, Is.EqualTo(RfqState.Settled));
        Assert.That(status.State.IsTerminal(), Is.True);
        Assert.That(status.Profile!.Preimage, Is.EqualTo("00ff"));
    }

    [Test]
    public async Task GetStatus_TreatsAnUnknownStateAsNonTerminal()
    {
        // Better to keep watching the chain than to stop on a word we do not recognise.
        var transport = TransportReturning(HttpStatusCode.OK, new JsonObject
        {
            ["v"] = 1, ["type"] = "rfq_status", ["rfq_id"] = RfqId, ["state"] = "reticulating_splines",
        });

        var status = await transport.GetStatusAsync<LightningSendStatusProfile>(RfqId);

        Assert.That(status!.State, Is.EqualTo(RfqState.Unknown));
        Assert.That(status.State.IsTerminal(), Is.False);
    }

    [Test]
    public async Task BaseAddress_WithoutATrailingSlash_KeepsItsPathPrefix()
    {
        // Relative-Uri resolution would otherwise drop "/solver" and post to the wrong path.
        var handler = new StubHandler(HttpStatusCode.Created, Quote());
        var transport = new HttpRfqTransport(new HttpClient(handler), new Uri("http://gateway.test/solver"));

        await transport.RequestQuoteAsync<LightningSendRequestProfile, LightningSendQuoteProfile>(LightningSendProfile.Request("lnbcrt1...", "ark1qexample", RfqId));

        Assert.That(handler.LastRequestUri!.AbsolutePath, Is.EqualTo("/solver/v1/swap"));
    }

    private static HttpRfqTransport TransportReturning(HttpStatusCode status, JsonObject payload) =>
        new(new HttpClient(new StubHandler(status, payload)), Solver);

    private static JsonObject Quote() => new()
    {
        ["v"] = 1,
        ["type"] = "rfq_quote",
        ["rfq_id"] = RfqId,
        ["pair"] = "arkade:BTC->lightning:BTC",
        ["from_amount"] = 50000,
        ["to_amount"] = 50000,
        ["solver_pubkey"] = "ae75000000000000000000000000000000000000000000000000000000000009",
        ["valid_until"] = 1800000900,
        ["refund_locktime"] = 1800605184,
        ["profile"] = new JsonObject
        {
            ["payment_hash"] = "b566a3eecce809896361988823cd2f423fe800e7b566a3eecce80989636198ab",
            ["lockup_address"] = "ark1qlockup",
        },
    };

    private sealed class StubHandler(HttpStatusCode status, JsonObject payload) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            });
        }
    }
}
