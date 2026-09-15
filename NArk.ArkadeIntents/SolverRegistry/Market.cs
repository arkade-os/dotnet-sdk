using System.Text.Json;
using System.Text.Json.Serialization;
using System.Numerics;
using NArk.ArkadeIntents.Rfq.Converters;

namespace NArk.ArkadeIntents.SolverRegistry;

/// <summary>
/// A v0/v1 market asset descriptor. Canonical identity includes the chain; names and tickers are display-only.
/// </summary>
public sealed class AssetDescriptor
{
    /// <summary>Canonical CAIP-19 id in source cards, or the legacy id in compatibility indexes.</summary>
    public required string Id { get; init; }

    /// <summary>Canonical identity retained beside the legacy id in compatibility indexes.</summary>
    public string? Caip19Id { get; init; }

    /// <summary>Canonical identity when published; otherwise the explicit legacy identifier.</summary>
    [JsonIgnore]
    public string CanonicalId => Caip19Id ?? Id;

    /// <summary>BTC or issued-asset identifier for legacy wallet APIs; external assets cannot be projected.</summary>
    [JsonIgnore]
    public string LegacyId => !CanonicalId.Contains('/') ? Id
        : AssetIdentifier.Parse(CanonicalId) is { Namespace: not "eip155" } asset
            ? asset.Asset.StartsWith("slip44:") ? "btc" : asset.Asset["asset:".Length..]
            : throw new NotSupportedException("An external asset has no legacy Arkade identifier.");

    /// <summary>Human-readable name (e.g. "Tether USD").</summary>
    public string? Name { get; init; }

    /// <summary>Display ticker (e.g. "USDT").</summary>
    public string? Ticker { get; init; }

    /// <summary>
    /// Decimal places of this asset's atomic unit: atomic units per display unit is
    /// 10^<c>decimals</c>.
    /// </summary>
    /// <remarks>
    /// Display only — pricing stays in atomic units and never reads this. Named after the asset
    /// registry metadata field it mirrors, which is also the JSON key the card carries.
    /// </remarks>
    public int Decimals { get; init; }
}

/// <summary>How to extract the scalar price out of a <see cref="SolverMarket.PriceFeed"/> response.</summary>
public sealed class PriceFeedSchema
{
    /// <summary>Feed format. Only <c>"json"</c> is defined in v0.</summary>
    public string Type { get; init; } = "json";

    /// <summary>RFC 6901 JSON Pointer to the scalar price value (e.g. <c>"/price"</c>).</summary>
    public required string PricePath { get; init; }
}

/// <summary>
/// A single market advertised by a solver (discovery v0 or v1). The same shape
/// appears inside a source <see cref="SolverCard"/> and, tagged with its solver, inside the
/// per-network index (<see cref="IndexedMarket"/>).
/// </summary>
public class SolverMarket
{
    /// <summary>Optional display label; empty when omitted. Never used as market identity.</summary>
    public string Pair { get; init; } = "";

    public required AssetDescriptor BaseAsset { get; init; }
    public required AssetDescriptor QuoteAsset { get; init; }

    /// <summary>
    /// Exact price-feed URL. Must be CORS-accessible for browser clients. Absent on a corridor
    /// market, where terms are negotiated per trade by RFQ rather than read off a feed.
    /// </summary>
    public string? PriceFeed { get; init; }

    /// <summary>How to read <see cref="PriceFeed"/>. Absent whenever that is.</summary>
    public PriceFeedSchema? PriceFeedSchema { get; init; }

    /// <summary>
    /// The rail the base side settles on. Absent means arkade, which every spot market is.
    /// </summary>
    /// <remarks>
    /// When exactly one side is on the arkade corridor it is this one, so equivalent corridor
    /// markets group under a single key.
    /// </remarks>
    public string? BaseCorridor { get; init; }

    /// <summary>
    /// The rail the quote side settles on — <c>"lightning"</c>, <c>"onchain"</c>. Absent means
    /// arkade, i.e. an ordinary spot market rather than a corridor.
    /// </summary>
    public string? QuoteCorridor { get; init; }

    /// <summary>Normalization factor: the raw feed scalar is divided by 10^<see cref="PriceDecimals"/>.</summary>
    public int PriceDecimals { get; init; }

    /// <summary>Solver spread, in basis points.</summary>
    public int FeeBps { get; init; }

    /// <summary>
    /// A flat component of the solver's fee, in <em>quote</em>-asset atomic units, charged on top
    /// of <see cref="FeeBps"/>.
    /// </summary>
    /// <remarks>
    /// Quote-denominated in both directions, matching <see cref="MinQuoteAmount"/> and
    /// <see cref="MaxQuoteAmount"/>, so a client converts it through the price when the maker
    /// receives base. On a same-asset corridor the two denominations coincide.
    /// <para>
    /// Absent on cards that charge proportionally only, which is why it is a nullable string rather
    /// than a number defaulting to zero: an unset field and a declared zero are the same charge, and
    /// treating a missing one as an error would refuse every card written before this existed.
    /// Serialized as a decimal string for the same reason as the amount bounds below.
    /// </para>
    /// </remarks>
    public string? FeeFlat { get; init; }

    /// <summary>The flat fee as a number, or zero when the card declares none.</summary>
    [JsonIgnore]
    public long FeeFlatAmount =>
        checked((long)FeeFlatAtomicAmount);

    /// <summary>The flat fee without narrowing its atomic units to Int64.</summary>
    [JsonIgnore]
    public BigInteger FeeFlatAtomicAmount => FeeFlat is null ? BigInteger.Zero
        : JsonSerializer.Deserialize<BigInteger>(JsonSerializer.Serialize(FeeFlat), AtomicJson);

    private static readonly JsonSerializerOptions AtomicJson = new() { Converters = { new AtomicAmountConverter() } };

    /// <summary>Minimum trade size, in base-asset units.</summary>
    /// <remarks>
    /// Serialized as a decimal string: these are base units of an asset whose precision the card
    /// itself declares, and a JSON number would silently lose the large ones.
    /// </remarks>
    [JsonIgnore]
    public long MinBaseAmount { get => checked((long)MinBaseAtomicAmount); init => MinBaseAtomicAmount = value; }

    /// <summary>Full-width minimum in base atomic units.</summary>
    [JsonPropertyName("min_base_amount"), JsonConverter(typeof(AtomicAmountConverter))]
    public BigInteger MinBaseAtomicAmount { get; init; }

    /// <summary>Maximum trade size, in base-asset units.</summary>
    [JsonIgnore]
    public long MaxBaseAmount { get => checked((long)MaxBaseAtomicAmount); init => MaxBaseAtomicAmount = value; }

    /// <summary>Full-width maximum in base atomic units.</summary>
    [JsonPropertyName("max_base_amount"), JsonConverter(typeof(AtomicAmountConverter))]
    public BigInteger MaxBaseAtomicAmount { get; init; }

    /// <summary>Minimum trade size, in quote-asset units — where a corridor states its bounds.</summary>
    [JsonIgnore]
    public long MinQuoteAmount { get => checked((long)MinQuoteAtomicAmount); init => MinQuoteAtomicAmount = value; }

    /// <summary>Full-width minimum in quote atomic units.</summary>
    [JsonPropertyName("min_quote_amount"), JsonConverter(typeof(AtomicAmountConverter))]
    public BigInteger MinQuoteAtomicAmount { get; init; }

    /// <summary>Maximum trade size, in quote-asset units.</summary>
    [JsonIgnore]
    public long MaxQuoteAmount { get => checked((long)MaxQuoteAtomicAmount); init => MaxQuoteAtomicAmount = value; }

    /// <summary>Full-width maximum in quote atomic units.</summary>
    [JsonPropertyName("max_quote_amount"), JsonConverter(typeof(AtomicAmountConverter))]
    public BigInteger MaxQuoteAtomicAmount { get; init; }

    /// <summary>The arkade corridor, which an absent per-side corridor means.</summary>
    public const string ArkadeCorridor = "arkade";

    /// <summary>A side's corridor from its canonical id, or legacy fields; maps bolt11/bitcoin to lightning/onchain.</summary>
    /// <param name="side">Which side to read.</param>
    /// <returns>The corridor name.</returns>
    public string CorridorOf(MarketSide side)
    {
        var id = (side == MarketSide.Base ? BaseAsset : QuoteAsset).CanonicalId;
        if (id.Contains('/')) return AssetIdentifier.Parse(id).Namespace switch
        {
            "bolt11" => "lightning", "bitcoin" => "onchain", var rail => rail,
        };
        return (side == MarketSide.Base ? BaseCorridor : QuoteCorridor) is { Length: > 0 } legacy
            ? legacy : ArkadeCorridor;
    }

    /// <summary>True when either side settles off the arkade corridor.</summary>
    /// <remarks>
    /// Such a market is negotiated per trade over RFQ rather than filled from the arkd stream, so
    /// the card's rendezvous fields are what make it reachable at all.
    /// </remarks>
    public bool IsCorridor =>
        CorridorOf(MarketSide.Base) != ArkadeCorridor || CorridorOf(MarketSide.Quote) != ArkadeCorridor;

    /// <summary>Both sides carry the same asset — the price is identically 1 and no feed applies.</summary>
    public bool IsSameAsset => !BaseAsset.CanonicalId.Contains('/') || !QuoteAsset.CanonicalId.Contains('/')
        ? BaseAsset.CanonicalId == QuoteAsset.CanonicalId
        : AssetIdentifier.Parse(BaseAsset.CanonicalId) is var left
          && AssetIdentifier.Parse(QuoteAsset.CanonicalId) is var right
          && left.ChainReference == right.ChainReference && left.Asset == right.Asset;

    /// <summary>One side's full CAIP-19 identity, or a corridor-qualified legacy id when no canonical identity was published.</summary>
    /// <param name="side">Which side to read.</param>
    /// <returns>The leg key.</returns>
    public string LegKey(MarketSide side)
    {
        var id = (side == MarketSide.Base ? BaseAsset : QuoteAsset).CanonicalId;
        return id.Contains('/') ? AssetIdentifier.Parse(id).Value : $"{CorridorOf(side)}:{id}";
    }

    /// <summary>
    /// The market's canonical identity: the corridor-qualified leg pair.
    /// </summary>
    /// <returns><c>&lt;base-corridor&gt;:&lt;base-id&gt;/&lt;quote-corridor&gt;:&lt;quote-id&gt;</c>.</returns>
    /// <remarks>
    /// Never the <see cref="Pair"/> label, and no longer the bare id pair: two BTC/BTC markets on
    /// different rails are different markets, and grouping them together offers a maker a Lightning
    /// corridor where they asked for an onchain one.
    /// </remarks>
    public string PairKey() => $"{LegKey(MarketSide.Base)}/{LegKey(MarketSide.Quote)}";

    /// <summary>The total fee this market charges on <paramref name="amount"/>, in its units.</summary>
    /// <param name="amount">The size being traded.</param>
    /// <returns>Basis points on the amount, plus the flat component.</returns>
    /// <remarks>
    /// <see cref="FeeBps"/> alone is not a ranking key once <see cref="FeeFlat"/> exists: a market
    /// with a lower spread and a flat fee is dearer at small sizes and cheaper at large ones.
    /// </remarks>
    public long TotalFeeOn(long amount) => checked((long)TotalFeeOn(new BigInteger(amount)));

    /// <summary>The fee in full-width atomic units, without intermediate Int64 overflow.</summary>
    public BigInteger TotalFeeOn(BigInteger amount) => amount * FeeBps / 10_000 + FeeFlatAtomicAmount;
}

/// <summary>A <see cref="SolverMarket"/> as published in the per-network index, tagged with its solver.</summary>
public sealed class IndexedMarket : SolverMarket
{
    /// <summary>The solver name that advertises this market.</summary>
    public required string Solver { get; init; }

    /// <summary>The solver's discovery x-only pubkey (hex), if the card carried one.</summary>
    public string? DiscoveryPubkey { get; init; }

    /// <summary>
    /// The solver card's transport map, propagated by the reducer when the card carries one.
    /// </summary>
    /// <remarks>
    /// Without it a <see cref="DiscoveryPubkey"/> names a solver nothing can dial: the protocol
    /// addresses parties by key and carries no URLs, so this is where "where" lives.
    /// </remarks>
    public SolverTransports? Transports { get; init; }
}

/// <summary>Which side of a market pair is meant.</summary>
public enum MarketSide
{
    /// <summary>The base side — the arkade leg, whenever exactly one side is arkade.</summary>
    Base,

    /// <summary>The quote side.</summary>
    Quote,
}

/// <summary>
/// Reads an integer that the card serializes as a decimal string, and tolerates a bare number.
/// </summary>
/// <remarks>
/// The registry writes amounts as strings on purpose — they are base units of assets whose precision
/// the card declares, and JSON's double-backed numbers cannot carry the large ones exactly. Accepting
/// both shapes means a hand-written or older card still reads.
/// </remarks>
internal sealed class NumericStringConverter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Number => reader.GetInt64(),
            JsonTokenType.String when long.TryParse(reader.GetString(), out var parsed) => parsed,
            JsonTokenType.String => throw new JsonException(
                $"expected an integer amount, got \"{reader.GetString()}\""),
            _ => throw new JsonException($"expected an integer amount, got {reader.TokenType}"),
        };

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
