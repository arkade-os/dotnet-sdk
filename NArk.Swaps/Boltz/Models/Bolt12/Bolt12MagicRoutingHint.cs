using System.Text.Json.Serialization;

namespace NArk.Swaps.Boltz.Models.Bolt12;

/// <summary>
/// Magic routing hint included in a <see cref="Bolt12FetchResponse"/>.
/// Corresponds to the <c>ReverseBip21</c> schema in the Boltz OpenAPI spec.
/// </summary>
public class Bolt12MagicRoutingHint
{
    /// <summary>BIP-21 URI for the reverse swap lockup address.</summary>
    [JsonPropertyName("bip21")]
    public string? Bip21 { get; set; }

    /// <summary>
    /// Schnorr signature of the address in the BIP-21, signed by the public
    /// key embedded in the routing hint. Used by the payer to verify the hint.
    /// </summary>
    [JsonPropertyName("signature")]
    public string? Signature { get; set; }
}
