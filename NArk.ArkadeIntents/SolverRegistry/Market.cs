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

    /// <summary>Number of decimal places in this asset's smallest unit.</summary>
    public int Precision { get; init; }
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
    /// The rail the quote side settles on — <c>"lightning"</c>, <c>"onchain"</c>. Absent means
    /// arkade, i.e. an ordinary spot market rather than a corridor.
    /// </summary>
    public string? QuoteCorridor { get; init; }

    /// <summary>Normalization factor: the raw feed scalar is divided by 10^<see cref="PriceDecimals"/>.</summary>
    public int PriceDecimals { get; init; }

    /// <summary>When true, the normalized price is inverted (base/quote direction flip).</summary>
    public bool Invert { get; init; }

    /// <summary>Solver spread, in basis points.</summary>
    public int FeeBps { get; init; }

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

    /// <summary>True when this market is a corridor rather than an arkade-to-arkade spot pair.</summary>
    public bool IsCorridor => !string.IsNullOrEmpty(QuoteCorridor);
}

/// <summary>A <see cref="SolverMarket"/> as published in the per-network index, tagged with its solver.</summary>
public sealed class IndexedMarket : SolverMarket
{
    /// <summary>The solver name that advertises this market.</summary>
    public required string Solver { get; init; }

    /// <summary>The solver's discovery x-only pubkey (hex), if the card carried one.</summary>
    public string? DiscoveryPubkey { get; init; }
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
