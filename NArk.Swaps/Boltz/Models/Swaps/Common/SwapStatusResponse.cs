using System.Text.Json.Serialization;

namespace NArk.Swaps.Boltz.Models.Swaps.Common;

public class SwapStatusResponse
{
    [JsonPropertyName("status")]
    public required string Status { get; set; }

    [JsonPropertyName("failureReason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FailureReason { get; set; }

    [JsonPropertyName("failureDetails")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FailureDetails { get; set; }

    [JsonPropertyName("transaction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SwapStatusTransaction? Transaction { get; set; }
}

/// <summary>
/// Transaction details included in swap status responses when a lockup tx exists.
/// </summary>
public class SwapStatusTransaction
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("hex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hex { get; set; }
}