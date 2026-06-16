using System.Text.Json.Serialization;

namespace NArk.Swaps.Boltz.Models.Bolt12;

/// <summary>
/// Request body for <c>PATCH /v2/lightning/{currency}/bolt12</c>.
/// Updates the webhook URL associated with a previously registered BOLT 12 offer.
/// </summary>
/// <remarks>
/// <see cref="Signature"/> must be a Schnorr signature of the SHA-256 hash of
/// the new <see cref="WebhookUrl"/>, or the literal string <c>"UPDATE"</c> when
/// <see cref="WebhookUrl"/> is omitted (i.e. removing the webhook). The signature
/// must be produced by the private key whose public key is embedded in the offer.
/// </remarks>
public class Bolt12UpdateOfferRequest
{
    /// <summary>The BOLT 12 offer to update (<c>lno1…</c>).</summary>
    [JsonPropertyName("offer")]
    public required string Offer { get; set; }

    /// <summary>
    /// New webhook URL, or <c>null</c> to remove the existing webhook.
    /// </summary>
    [JsonPropertyName("url")]
    public string? WebhookUrl { get; set; }

    /// <summary>
    /// Schnorr signature authorising the update. Must be the signature of the
    /// SHA-256 hash of <see cref="WebhookUrl"/>, or <c>"UPDATE"</c> when no
    /// URL is provided.
    /// </summary>
    [JsonPropertyName("signature")]
    public required string Signature { get; set; }
}
