using System.Text.Json.Serialization;

namespace NArk.Swaps.Boltz.Models.Swaps.Submarine;

public class SubmarineRequest
{
    [JsonPropertyName("from")]
    public required string From { get; set; } // e.g., "BTC"

    [JsonPropertyName("to")]
    public required string To { get; set; } // e.g., "LNBTC"

    [JsonPropertyName("invoice")]
    public required string Invoice { get; set; }

    [JsonPropertyName("refundPublicKey")]
    public required string RefundPublicKey { get; set; }

}