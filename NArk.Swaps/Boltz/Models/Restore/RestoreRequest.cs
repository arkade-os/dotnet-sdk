using System.Text.Json.Serialization;

namespace NArk.Swaps.Boltz.Models.Restore;

/// <summary>
/// Request body for the Boltz /v2/swap/restore endpoint.
/// Supports single key, multiple keys, or XPUB-based restoration.
/// </summary>
public record RestoreRequest
{
    /// <summary>
    /// Single public key (hex-encoded) to search for in swaps.
    /// </summary>
    [JsonPropertyName("publicKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PublicKey { get; init; }

    /// <summary>
    /// Array of public keys (hex-encoded) to search for in swaps.
    /// </summary>
    [JsonPropertyName("publicKeys")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? PublicKeys { get; init; }
}
