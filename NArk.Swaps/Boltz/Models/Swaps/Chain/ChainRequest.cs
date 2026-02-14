using System.Text.Json.Serialization;

namespace NArk.Swaps.Boltz.Models.Swaps.Chain;

/// <summary>
/// Request for POST /v2/swap/chain — create a new chain swap.
/// </summary>
public class ChainRequest
{
    [JsonPropertyName("from")]
    public required string From { get; set; }

    [JsonPropertyName("to")]
    public required string To { get; set; }

    [JsonPropertyName("preimageHash")]
    public required string PreimageHash { get; set; }

    [JsonPropertyName("claimPublicKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClaimPublicKey { get; set; }

    [JsonPropertyName("refundPublicKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RefundPublicKey { get; set; }

    [JsonPropertyName("userLockAmount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long UserLockAmount { get; set; }

    [JsonPropertyName("serverLockAmount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long ServerLockAmount { get; set; }

    [JsonPropertyName("pairHash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PairHash { get; set; }

    [JsonPropertyName("referralId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReferralId { get; set; }
}
