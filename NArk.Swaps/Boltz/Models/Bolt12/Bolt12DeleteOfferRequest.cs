using System.Text.Json.Serialization;

namespace NArk.Swaps.Boltz.Models.Bolt12;

/// <summary>
/// Request body for <c>POST /v2/lightning/{currency}/bolt12/delete</c>.
/// Removes a previously registered BOLT 12 offer from Boltz.
/// </summary>
/// <remarks>
/// <see cref="Signature"/> must be a Schnorr signature of the SHA-256 hash of
/// the literal string <c>"DELETE"</c>, produced by the private key whose public
/// key is embedded in <see cref="Offer"/>.
/// </remarks>
public class Bolt12DeleteOfferRequest
{
    /// <summary>The BOLT 12 offer to delete (<c>lno1…</c>).</summary>
    [JsonPropertyName("offer")]
    public required string Offer { get; set; }

    /// <summary>
    /// Schnorr signature authorising the deletion, as described in the remarks.
    /// </summary>
    [JsonPropertyName("signature")]
    public required string Signature { get; set; }
}
