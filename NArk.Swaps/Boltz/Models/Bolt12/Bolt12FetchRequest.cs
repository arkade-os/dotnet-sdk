using System.Text.Json.Serialization;

namespace NArk.Swaps.Boltz.Models.Bolt12;

/// <summary>
/// Request body for <c>POST /v2/lightning/{currency}/bolt12/fetch</c>.
/// Asks Boltz to fetch a BOLT 12 invoice from the given offer so the caller
/// can use it in a submarine swap without needing a direct connection to the
/// payee's Lightning node.
/// </summary>
public class Bolt12FetchRequest
{
    /// <summary>The BOLT 12 offer to fetch an invoice from (<c>lno1…</c>).</summary>
    [JsonPropertyName("offer")]
    public required string Offer { get; set; }

    /// <summary>
    /// Amount in satoshis for the invoice that should be fetched.
    /// <c>null</c> when the offer already encodes a fixed amount — Boltz will
    /// use the amount embedded in the offer.
    /// </summary>
    [JsonPropertyName("amount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Amount { get; set; }

    /// <summary>Optional note to include in the invoice request.</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }
}
