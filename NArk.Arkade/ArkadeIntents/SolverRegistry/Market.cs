namespace NArk.Arkade.NonInteractiveSwaps;

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

    /// <summary>Exact price-feed URL. Must be CORS-accessible for browser clients.</summary>
    public required string PriceFeed { get; init; }

    public required PriceFeedSchema PriceFeedSchema { get; init; }

    /// <summary>Normalization factor: the raw feed scalar is divided by 10^<see cref="PriceDecimals"/>.</summary>
    public int PriceDecimals { get; init; }

    /// <summary>When true, the normalized price is inverted (base/quote direction flip).</summary>
    public bool Invert { get; init; }

    /// <summary>Solver spread, in basis points.</summary>
    public int FeeBps { get; init; }

    /// <summary>Minimum trade size, in base-asset units.</summary>
    public long MinBaseAmount { get; init; }

    /// <summary>Maximum trade size, in base-asset units.</summary>
    public long MaxBaseAmount { get; init; }
}

/// <summary>A <see cref="SolverMarket"/> as published in the per-network index, tagged with its solver.</summary>
public sealed class IndexedMarket : SolverMarket
{
    /// <summary>The solver name that advertises this market.</summary>
    public required string Solver { get; init; }

    /// <summary>The solver's discovery x-only pubkey (hex), if the card carried one.</summary>
    public string? DiscoveryPubkey { get; init; }
}
