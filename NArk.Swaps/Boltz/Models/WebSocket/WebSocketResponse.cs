using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NArk.Swaps.Boltz.Models.WebSocket;

public class WebSocketResponse
{
    [JsonPropertyName("event")]
    public required string Event { get; set; }

    [JsonPropertyName("channel")]
    public string? Channel { get; set; }

    [JsonPropertyName("args")]
    public JsonArray Args { get; set; } = [];
}
