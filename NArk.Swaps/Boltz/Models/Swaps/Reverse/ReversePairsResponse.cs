using System.Text.Json.Serialization;

namespace NArk.Swaps.Boltz.Models.Swaps.Reverse;

public class ReversePairsResponse
{
    [JsonPropertyName("BTC")]
    public required ReversePairInfo BTC { get; set; }
}

public class ReversePairInfo
{
    [JsonPropertyName("ARK")]
    public required ReversePairDetails ARK { get; set; }
}

public class ReversePairDetails
{
    [JsonPropertyName("fees")]
    public required ReverseFeeInfo Fees { get; set; }

    [JsonPropertyName("limits")]
    public required ReverseLimitsInfo Limits { get; set; }
}

public class ReverseFeeInfo
{
    [JsonPropertyName("percentage")]
    public decimal Percentage { get; set; }

    [JsonPropertyName("minerFees")]
    public ReverseMinerFeesInfo? MinerFees { get; set; }
}

public class ReverseMinerFeesInfo
{
    [JsonPropertyName("claim")]
    public long Claim { get; set; }

    [JsonPropertyName("lockup")]
    public long Lockup { get; set; }
}

public class ReverseLimitsInfo
{
    [JsonPropertyName("minimal")]
    public long Minimal { get; set; }

    [JsonPropertyName("maximal")]
    public long Maximal { get; set; }
}
