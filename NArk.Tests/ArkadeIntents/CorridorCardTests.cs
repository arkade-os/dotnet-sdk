using System.Text.Json;
using NArk.ArkadeIntents.SolverRegistry;

namespace NArk.Tests.ArkadeIntents;

/// <summary>
/// Reading the registry card a Lightning corridor solver actually publishes.
/// </summary>
/// <remarks>
/// The card is the only place a solver's identity, its rendezvous relays and its per-corridor limits
/// are stated with provenance — it is signed and git-reviewed, unlike anything on the RFQ wire. A
/// client that cannot read one cannot discover a solver at all, so the shape it publishes is worth
/// pinning rather than assuming.
/// </remarks>
[TestFixture]
public class CorridorCardTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>
    /// Exactly the card shape the reference solver publishes to the registry.
    /// A corridor market has no price feed — terms come from RFQ, not from a feed — and states its
    /// limits on the quote side, as strings.
    /// </summary>
    private const string CorridorCard = """
    {
      "version": 0,
      "name": "testsolver",
      "discovery_pubkey": "df5e3a677c20ff3af3c1701e5ed75aa7cc1e3ff8069ea4a8df5012494d7af6eb",
      "transports": { "nostr": { "relays": ["wss://relay.example.com"] } },
      "markets": [
        {
          "pair": "BTC/lightning:BTC",
          "base_asset": { "id": "btc", "name": "Bitcoin", "ticker": "BTC", "decimals": 8 },
          "quote_asset": { "id": "btc", "name": "Bitcoin", "ticker": "BTC", "decimals": 8 },
          "quote_corridor": "lightning",
          "fee_bps": 30,
          "min_base_amount": "0",
          "max_base_amount": "0",
          "min_quote_amount": "1000",
          "max_quote_amount": "1000000"
        }
      ],
      "sig": "aa"
    }
    """;

    [Test]
    public void ACorridorCard_Deserializes()
    {
        var card = JsonSerializer.Deserialize<SolverCard>(CorridorCard, JsonOptions);

        Assert.That(card, Is.Not.Null);
        Assert.That(card!.Markets, Has.Count.EqualTo(1));
    }

    [Test]
    public void ACorridorCard_CarriesTheRendezvousRelays()
    {
        // Without these the discovery pubkey names a solver nothing can reach: the protocol carries
        // no URLs, so the card is where "where" lives.
        var card = JsonSerializer.Deserialize<SolverCard>(CorridorCard, JsonOptions);

        Assert.That(card!.Transports?.Nostr?.Relays, Is.EqualTo(new[] { "wss://relay.example.com" }));
    }

    [Test]
    public void ACorridorCard_CarriesItsLimits()
    {
        var card = JsonSerializer.Deserialize<SolverCard>(CorridorCard, JsonOptions);
        var market = card!.Markets[0];

        Assert.Multiple(() =>
        {
            Assert.That(market.FeeBps, Is.EqualTo(30));
            // A corridor states its bounds on the quote side; the base side is left at zero.
            Assert.That(market.MinQuoteAmount, Is.EqualTo(1000));
            Assert.That(market.MaxQuoteAmount, Is.EqualTo(1_000_000));
        });
    }
}
