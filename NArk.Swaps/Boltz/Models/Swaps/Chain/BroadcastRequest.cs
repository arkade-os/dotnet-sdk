using System.Text.Json.Serialization;

namespace NArk.Swaps.Boltz.Models.Swaps.Chain;

/// <summary>
/// Request for POST /v2/chain/{currency}/transaction — broadcast a raw transaction.
/// </summary>
public class BroadcastRequest
{
    [JsonPropertyName("hex")]
    public required string Hex { get; set; }
}

/// <summary>
/// Response from POST /v2/chain/{currency}/transaction.
/// </summary>
public class BroadcastResponse
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }
}
