using System.Text.Json.Serialization;

namespace NArk.Swaps.Boltz.Models.Bolt12;

/// <summary>
/// Response from <c>GET /v2/lightning/{currency}/bolt12/{receiving}</c>.
/// Contains the parameters required when constructing a BOLT 12 offer that
/// routes payments through Boltz to the specified receiving chain.
/// </summary>
public class Bolt12OfferParams
{
    /// <summary>
    /// Minimum CLTV (CheckLockTimeVerify) delta that the offer must encode.
    /// Payers use this to ensure the payment route has sufficient time to settle.
    /// </summary>
    [JsonPropertyName("minCltv")]
    public int MinCltv { get; set; }
}
