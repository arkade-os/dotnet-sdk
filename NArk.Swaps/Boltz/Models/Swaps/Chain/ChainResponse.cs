using System.Text.Json.Serialization;

namespace NArk.Swaps.Boltz.Models.Swaps.Chain;

/// <summary>
/// Response from POST /v2/swap/chain — chain swap creation result.
/// </summary>
public class ChainResponse
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("claimDetails")]
    public ChainSwapData? ClaimDetails { get; set; }

    [JsonPropertyName("lockupDetails")]
    public ChainSwapData? LockupDetails { get; set; }
}

/// <summary>
/// Details for one side of a chain swap (either claim or lockup).
/// </summary>
public class ChainSwapData
{
    [JsonPropertyName("lockupAddress")]
    public required string LockupAddress { get; set; }

    [JsonPropertyName("serverPublicKey")]
    public string? ServerPublicKey { get; set; }

    [JsonPropertyName("timeoutBlockHeight")]
    public int TimeoutBlockHeight { get; set; }

    [JsonPropertyName("amount")]
    public long Amount { get; set; }

    [JsonPropertyName("swapTree")]
    public ChainSwapTree? SwapTree { get; set; }

    [JsonPropertyName("blindingKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BlindingKey { get; set; }

    [JsonPropertyName("bip21")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Bip21 { get; set; }
}
