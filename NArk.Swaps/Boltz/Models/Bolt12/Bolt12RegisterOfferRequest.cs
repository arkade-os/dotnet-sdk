using System.Text.Json.Serialization;

namespace NArk.Swaps.Boltz.Models.Bolt12;

/// <summary>
/// Request body for <c>POST /v2/lightning/{currency}/bolt12</c>.
/// Registers a BOLT 12 offer with Boltz so that incoming Lightning payments
/// to that offer are converted to Arkade VTXOs for the offer's owner.
/// </summary>
/// <remarks>
/// The caller must already hold a BOLT 12-capable Lightning node (e.g. CLN or
/// LDK) that generated <see cref="Offer"/>. Boltz calls the optional
/// <see cref="WebhookUrl"/> to retrieve a fresh invoice each time a payer
/// tries to pay the offer. If <see cref="WebhookUrl"/> is omitted, Boltz uses
/// its own invoice-fetching mechanism.
/// </remarks>
public class Bolt12RegisterOfferRequest
{
    /// <summary>The BOLT 12 offer to register (<c>lno1…</c>).</summary>
    [JsonPropertyName("offer")]
    public required string Offer { get; set; }

    /// <summary>
    /// Optional webhook URL that Boltz calls to fetch invoices for the offer.
    /// Must be reachable by the Boltz server.
    /// </summary>
    [JsonPropertyName("url")]
    public string? WebhookUrl { get; set; }
}
