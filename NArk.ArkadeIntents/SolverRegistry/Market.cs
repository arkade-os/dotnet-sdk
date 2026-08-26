using System.Text.Json;
using System.Text.Json.Serialization;

namespace NArk.ArkadeIntents.SolverRegistry;

/// <summary>
/// An asset descriptor (base or quote side of a market), per the Arkade Market Discovery
/// Protocol v0. JSON keys are snake_case (<c>id</c>, <c>name</c>, <c>ticker</c>, <c>precision</c>).
/// </summary>
public sealed class AssetDescriptor
{
    /// <summary>Asset id — <c>"btc"</c> for Bitcoin, or the asset-id hex for an Arkade asset. This is the pair identity, not the ticker.</summary>
    public required string Id { get; init; }

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
/// A single market advertised by a solver (Arkade Market Discovery Protocol v0). The same shape
/// appears inside a source <see cref="SolverCard"/> and, tagged with its solver, inside the
/// per-network index (<see cref="IndexedMarket"/>).
/// </summary>
public class SolverMarket
{
    /// <summary>Display label (e.g. "BTC/USDT"). Identity is the <c>base_asset.id</c>/<c>quote_asset.id</c> pair, not this.</summary>
    public required string Pair { get; init; }

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
    public long FeeFlatAmount =>
        long.TryParse(FeeFlat, out var flat) && flat > 0 ? flat : 0;

    /// <summary>Minimum trade size, in base-asset units.</summary>
    /// <remarks>
    /// Serialized as a decimal string: these are base units of an asset whose precision the card
    /// itself declares, and a JSON number would silently lose the large ones.
    /// </remarks>
    [JsonConverter(typeof(NumericStringConverter))]
    public long MinBaseAmount { get; init; }

    /// <summary>Maximum trade size, in base-asset units.</summary>
    [JsonConverter(typeof(NumericStringConverter))]
    public long MaxBaseAmount { get; init; }

    /// <summary>Minimum trade size, in quote-asset units — where a corridor states its bounds.</summary>
    [JsonConverter(typeof(NumericStringConverter))]
    public long MinQuoteAmount { get; init; }

    /// <summary>Maximum trade size, in quote-asset units.</summary>
    [JsonConverter(typeof(NumericStringConverter))]
    public long MaxQuoteAmount { get; init; }

    /// <summary>The arkade corridor, which an absent per-side corridor means.</summary>
    public const string ArkadeCorridor = "arkade";

    /// <summary>A side's corridor, defaulting an absent one to <see cref="ArkadeCorridor"/>.</summary>
    /// <param name="side">Which side to read.</param>
    /// <returns>The corridor name.</returns>
    public string CorridorOf(MarketSide side) =>
        (side == MarketSide.Base ? BaseCorridor : QuoteCorridor) is { Length: > 0 } rail
            ? rail
            : ArkadeCorridor;

    /// <summary>True when either side settles off the arkade corridor.</summary>
    /// <remarks>
    /// Such a market is negotiated per trade over RFQ rather than filled from the arkd stream, so
    /// the card's rendezvous fields are what make it reachable at all.
    /// </remarks>
    public bool IsCorridor =>
        CorridorOf(MarketSide.Base) != ArkadeCorridor || CorridorOf(MarketSide.Quote) != ArkadeCorridor;

    /// <summary>Both sides carry the same asset — the price is identically 1 and no feed applies.</summary>
    public bool IsSameAsset => BaseAsset.Id == QuoteAsset.Id;

    /// <summary>One side's canonical leg identity, <c>&lt;corridor&gt;:&lt;asset-id&gt;</c>.</summary>
    /// <param name="side">Which side to read.</param>
    /// <returns>The leg key.</returns>
    public string LegKey(MarketSide side) =>
        $"{CorridorOf(side)}:{(side == MarketSide.Base ? BaseAsset.Id : QuoteAsset.Id)}";

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
    public long TotalFeeOn(long amount) => amount * FeeBps / 10_000 + FeeFlatAmount;
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
