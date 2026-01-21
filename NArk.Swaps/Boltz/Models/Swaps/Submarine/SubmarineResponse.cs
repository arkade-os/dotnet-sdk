using System.Text.Json.Serialization;
using NArk.Swaps.Boltz.Models.Swaps.Reverse;

namespace NArk.Swaps.Boltz.Models.Swaps.Submarine
{
    public class SubmarineResponse
    {
        [JsonPropertyName("id")] public required string Id { get; set; }

        [JsonPropertyName("address")] public required string Address { get; set; }

        [JsonPropertyName("expectedAmount")] public long ExpectedAmount { get; set; }

        [JsonPropertyName("claimPublicKey")] public required string ClaimPublicKey { get; set; }

        [JsonPropertyName("acceptZeroConf")] public bool AcceptZeroConf { get; set; }

        [JsonPropertyName("timeoutBlockHeights")]
        public required TimeoutBlockHeights TimeoutBlockHeights { get; set; }
    }
}
