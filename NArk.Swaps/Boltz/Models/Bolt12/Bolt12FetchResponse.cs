using System.Text.Json.Serialization;

namespace NArk.Swaps.Boltz.Models.Bolt12;

/// <summary>
/// Response from <c>POST /v2/lightning/{currency}/bolt12/fetch</c>.
/// </summary>
public class Bolt12FetchResponse
{
    /// <summary>
    /// The fetched BOLT 12 invoice string (<c>lni1…</c>).
    /// Pass this directly to <c>POST /v2/swap/submarine</c> as the
    /// <c>invoice</c> field to initiate a submarine swap.
    /// </summary>
    [JsonPropertyName("invoice")]
    public required string Invoice { get; set; }

    /// <summary>
    /// Optional magic routing hint returned by Boltz for BOLT 12 reverse swaps.
    /// Contains a BIP-21 URI and a Schnorr signature for the embedded address.
    /// May be <c>null</c> when the offer does not require a routing hint.
    /// </summary>
    [JsonPropertyName("magicRoutingHint")]
    public Bolt12MagicRoutingHint? MagicRoutingHint { get; set; }
}
