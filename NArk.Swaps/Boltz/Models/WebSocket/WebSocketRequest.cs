using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NArk.Swaps.Boltz.Models.WebSocket;

public class WebSocketRequest
{
    [JsonPropertyName("op")]
    public required string Operation { get; set; } // e.g., "subscribe", "unsubscribe"

    [JsonPropertyName("channel")]
    public required string Channel { get; set; } // e.g., "swap.update"

    [JsonPropertyName("args")]
    public required JsonArray Args { get; set; } // e.g., array of swap IDs
}
