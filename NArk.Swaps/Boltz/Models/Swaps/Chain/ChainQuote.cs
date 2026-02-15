using System.Text.Json.Serialization;

namespace NArk.Swaps.Boltz.Models.Swaps.Chain;

/// <summary>
/// Response from GET /v2/swap/chain/{id}/quote and request for POST /v2/swap/chain/{id}/quote.
/// Used for renegotiating chain swap amounts when lockup amount doesn't match.
/// </summary>
public class ChainQuote
{
    [JsonPropertyName("amount")]
    public long Amount { get; set; }
}
