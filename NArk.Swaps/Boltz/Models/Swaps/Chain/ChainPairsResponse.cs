using System.Text.Json.Serialization;

namespace NArk.Swaps.Boltz.Models.Swaps.Chain;

/// <summary>
/// Response for GET /v2/swap/chain — chain swap pairs with limits and fees.
/// Structure: { "BTC": { "ARK": { ... } }, "ARK": { "BTC": { ... } } }
/// </summary>
public class ChainPairsResponse
{
    [JsonPropertyName("BTC")]
    public ChainPairCurrencyInfo? BTC { get; set; }

    [JsonPropertyName("ARK")]
    public ChainPairCurrencyInfo? ARK { get; set; }
}

public class ChainPairCurrencyInfo
{
    [JsonPropertyName("BTC")]
    public ChainPairDetails? BTC { get; set; }

    [JsonPropertyName("ARK")]
    public ChainPairDetails? ARK { get; set; }
}

public class ChainPairDetails
{
    [JsonPropertyName("hash")]
    public string? Hash { get; set; }

    [JsonPropertyName("rate")]
    public double Rate { get; set; }

    [JsonPropertyName("limits")]
    public required ChainLimitsInfo Limits { get; set; }

    [JsonPropertyName("fees")]
    public required ChainFeeInfo Fees { get; set; }
}

public class ChainLimitsInfo
{
    [JsonPropertyName("minimal")]
    public long Minimal { get; set; }

    [JsonPropertyName("maximal")]
    public long Maximal { get; set; }

    [JsonPropertyName("maximalZeroConf")]
    public long MaximalZeroConf { get; set; }
}

public class ChainFeeInfo
{
    [JsonPropertyName("percentage")]
    public decimal Percentage { get; set; }

    [JsonPropertyName("minerFees")]
    public required ChainMinerFeesInfo MinerFees { get; set; }
}

public class ChainMinerFeesInfo
{
    [JsonPropertyName("server")]
    public long Server { get; set; }

    [JsonPropertyName("user")]
    public required ChainUserMinerFees User { get; set; }
}

public class ChainUserMinerFees
{
    [JsonPropertyName("claim")]
    public long Claim { get; set; }

    [JsonPropertyName("lockup")]
    public long Lockup { get; set; }
}
