using System.Text.Json.Serialization;

namespace NArk.Core.Models;

public class Messages
{
    public class DeleteIntentMessage
    {
        // expire_at: nowSeconds + 2 * 60, // valid for 2 minutes

        [JsonPropertyName("type")]
        [JsonPropertyOrder(0)]
        public required string Type { get; set; }

        [JsonPropertyName("expire_at")]
        [JsonPropertyOrder(1)]
        
        public long ExpireAt { get; set; }
    }

    public class RegisterIntentMessage
    {
        // type: "register",
        // onchain_output_indexes: onchainOutputsIndexes,
        // valid_at: nowSeconds,
        // expire_at: nowSeconds + 2 * 60, // valid for 2 minutes
        // cosigners_public_keys: cosignerPubKeys,

        [JsonPropertyName("type")]
        [JsonPropertyOrder(0)]
        public required string Type { get; init; }

        [JsonPropertyName("onchain_output_indexes")]
        [JsonPropertyOrder(1)]
        public required int[] OnchainOutputsIndexes { get; init; }

        [JsonPropertyName("valid_at")]
        [JsonPropertyOrder(2)]
        public long ValidAt { get; init; }

        [JsonPropertyName("expire_at")]
        [JsonPropertyOrder(3)]
        public long ExpireAt { get; init; }

        [JsonPropertyName("cosigners_public_keys")]
        [JsonPropertyOrder(4)]
        public required string[] CosignersPublicKeys { get; init; }
    }
}