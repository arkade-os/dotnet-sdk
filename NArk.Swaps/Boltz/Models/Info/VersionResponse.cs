using System.Text.Json.Serialization;

namespace NArk.Swaps.Boltz.Models.Info;

public class VersionResponse
{
    [JsonPropertyName("version")]
    public required string Version { get; set; }
}
