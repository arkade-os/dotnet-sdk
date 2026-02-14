using System.Text.Json.Serialization;

namespace NArk.Swaps.Boltz.Models.Swaps.Chain;

/// <summary>
/// Tapscript tree for a chain swap HTLC.
/// BTC-side HTLCs have claim + refund leaves with a MuSig2 key-path.
/// </summary>
public class ChainSwapTree
{
    [JsonPropertyName("claimLeaf")]
    public required ChainSwapTreeLeaf ClaimLeaf { get; set; }

    [JsonPropertyName("refundLeaf")]
    public required ChainSwapTreeLeaf RefundLeaf { get; set; }
}

/// <summary>
/// A single tapscript leaf in a chain swap tree.
/// </summary>
public class ChainSwapTreeLeaf
{
    /// <summary>
    /// Tapscript version (192 = 0xc0 = tapscript v1).
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; set; }

    /// <summary>
    /// Hex-encoded script for this leaf.
    /// </summary>
    [JsonPropertyName("output")]
    public required string Output { get; set; }
}
