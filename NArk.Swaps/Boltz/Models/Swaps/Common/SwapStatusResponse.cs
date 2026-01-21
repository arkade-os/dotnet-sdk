using System.Text.Json.Serialization;

namespace NArk.Swaps.Boltz.Models.Swaps.Common;

public class SwapStatusResponse
{
    [JsonPropertyName("status")]
    public required string Status { get; set; }


}