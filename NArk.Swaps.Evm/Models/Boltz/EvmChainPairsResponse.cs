using System.Text.Json.Serialization;
using NArk.Swaps.Boltz.Models.Swaps.Chain;

namespace NArk.Swaps.Evm.Models;

/// <summary>
/// Own generic dictionary shape for <c>GET /v2/swap/chain</c> (chain-swap pairs).
/// <c>NArk.Swaps.Boltz.Models.Swaps.Chain.ChainPairsResponse</c> hardcodes only
/// <c>BTC</c>/<c>ARK</c> top-level keys, so it can't see a <c>TBTC</c> entry — this
/// reuses its generic leaf DTOs (<see cref="ChainLimitsInfo"/>, <see cref="ChainFeeInfo"/>)
/// without needing to modify that file for our sake.
/// </summary>
public class EvmChainPairDetails
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
