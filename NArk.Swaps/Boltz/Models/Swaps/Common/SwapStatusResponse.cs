using System.Text.Json;
using System.Text.Json.Serialization;

namespace NArk.Swaps.Boltz.Models.Swaps.Common;

public class SwapStatusResponse
{
    [JsonPropertyName("status")]
    public required string Status { get; set; }

    [JsonPropertyName("failureReason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FailureReason { get; set; }

    /// <summary>
    /// Boltz returns this as a structured object (e.g.
    /// <c>{"actual":51353,"expected":50353}</c> for transaction.lockupFailed),
    /// not a string. Kept as <see cref="JsonElement"/> so we can safely
    /// round-trip arbitrary shapes; callers that want strongly-typed
    /// access should deserialize from this element themselves.
    /// </summary>
    [JsonPropertyName("failureDetails")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? FailureDetails { get; set; }

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