namespace NArk.Swaps.Boltz.Models;

public class BoltzClientOptions
{
    public required string BoltzUrl { get; set; }
    public required string WebsocketUrl { get; set; }
    /// <summary>
    /// URL for the Boltz sidecar API (boltzr). Some endpoints like /v2/swap/restore
    /// are served by the sidecar, not the main API. Falls back to BoltzUrl if not set.
    /// </summary>
    public string? SidecarUrl { get; set; }
}