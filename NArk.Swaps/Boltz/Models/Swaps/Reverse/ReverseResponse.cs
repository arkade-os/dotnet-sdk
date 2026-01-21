using System.Text.Json.Serialization;

namespace NArk.Swaps.Boltz.Models.Swaps.Reverse;

public class ReverseResponse
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("lockupAddress")]
    public required string LockupAddress { get; set; }

    [JsonPropertyName("refundPublicKey")]
    public required string RefundPublicKey { get; set; }

    [JsonPropertyName("timeoutBlockHeights")]
    public required TimeoutBlockHeights TimeoutBlockHeights { get; set; }

    [JsonPropertyName("invoice")]
    public required string Invoice { get; set; }


}