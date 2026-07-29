using System.Text.Json.Serialization;

namespace NArk.Swaps.Evm.Models;

/// <summary>
/// Own request DTO for <c>POST /v2/swap/chain</c> covering the <c>claimAddress</c> field
/// (EVM claim address) that Boltz's API supports but
/// <c>NArk.Swaps.Boltz.Models.Swaps.Chain.ChainRequest</c> doesn't model yet — used only for
/// the ARK -&gt; EvmArbitrum direction (where we ARE the EVM claimer). The reverse direction
/// (EvmArbitrum -&gt; ARK) needs no EVM-specific field and reuses the existing
/// <c>ChainRequest</c>/<see cref="Boltz.Client.BoltzClient.CreateChainSwapAsync"/> as-is.
/// </summary>
public class EvmChainCreateRequest
{
    [JsonPropertyName("from")]
    public required string From { get; set; }

    [JsonPropertyName("to")]
    public required string To { get; set; }

    [JsonPropertyName("preimageHash")]
    public required string PreimageHash { get; set; }

    [JsonPropertyName("refundPublicKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RefundPublicKey { get; set; }

    [JsonPropertyName("claimAddress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClaimAddress { get; set; }

    [JsonPropertyName("userLockAmount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long UserLockAmount { get; set; }

    [JsonPropertyName("referralId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReferralId { get; set; }
}
