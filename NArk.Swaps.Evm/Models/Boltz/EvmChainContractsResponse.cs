using System.Text.Json.Serialization;

namespace NArk.Swaps.Evm.Models;

/// <summary>
/// Response body of Boltz's <c>GET /v2/chain/{currency}/contracts</c> — resolves the
/// deployed <c>EtherSwap</c>/<c>ERC20Swap</c> contract addresses and chain id for a
/// supported EVM chain (e.g. <c>"arbitrum"</c>).
/// </summary>
public class EvmChainContractsResponse
{
    [JsonPropertyName("network")]
    public EvmNetworkInfo Network { get; set; } = null!;

    [JsonPropertyName("tokens")]
    public Dictionary<string, string> Tokens { get; set; } = new();

    [JsonPropertyName("swapContracts")]
    public EvmSwapContracts SwapContracts { get; set; } = null!;
}

public class EvmNetworkInfo
{
    [JsonPropertyName("chainId")]
    public long ChainId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public class EvmSwapContracts
{
    [JsonPropertyName("EtherSwap")]
    public string EtherSwap { get; set; } = null!;

    [JsonPropertyName("ERC20Swap")]
    public string Erc20Swap { get; set; } = null!;
}
