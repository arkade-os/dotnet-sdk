using System.Text.Json.Serialization;

namespace NArk.Swaps.Boltz.Models.Swaps.Chain;

/// <summary>
/// Request for POST /v2/swap/chain/{id}/claim — submit claim signature + our unsigned tx.
/// </summary>
public class ChainClaimRequest
{
    /// <summary>
    /// Preimage (hex) to prove payment knowledge. Required for claiming.
    /// </summary>
    [JsonPropertyName("preimage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Preimage { get; set; }

    /// <summary>
    /// Our partial signature for Boltz's claim transaction (cross-signing).
    /// </summary>
    [JsonPropertyName("signature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PartialSignatureData? Signature { get; set; }

    /// <summary>
    /// Our unsigned transaction for Boltz to partially sign.
    /// </summary>
    [JsonPropertyName("toSign")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ToSignData? ToSign { get; set; }
}

/// <summary>
/// MuSig2 partial signature (our nonce + partial sig).
/// </summary>
public class PartialSignatureData
{
    [JsonPropertyName("pubNonce")]
    public required string PubNonce { get; set; }

    [JsonPropertyName("partialSignature")]
    public required string PartialSignature { get; set; }
}

/// <summary>
/// Unsigned transaction data for Boltz to co-sign via MuSig2.
/// </summary>
public class ToSignData
{
    [JsonPropertyName("pubNonce")]
    public required string PubNonce { get; set; }

    [JsonPropertyName("transaction")]
    public required string Transaction { get; set; }

    [JsonPropertyName("index")]
    public int Index { get; set; }
}
