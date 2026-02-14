using System.Text.Json.Serialization;

namespace NArk.Swaps.Boltz.Models.Swaps.Chain;

/// <summary>
/// Request for POST /v2/swap/chain/{id}/refund — BTC-side cooperative refund via MuSig2.
/// </summary>
public class ChainRefundRequest
{
    [JsonPropertyName("pubNonce")]
    public required string PubNonce { get; set; }

    [JsonPropertyName("transaction")]
    public required string Transaction { get; set; }

    [JsonPropertyName("index")]
    public int Index { get; set; }
}

/// <summary>
/// Request for POST /v2/swap/chain/{id}/refund/ark — Ark-side cooperative refund via PSBT.
/// </summary>
public class ChainArkRefundRequest
{
    [JsonPropertyName("transaction")]
    public required string Transaction { get; set; }

    [JsonPropertyName("checkpoint")]
    public required string Checkpoint { get; set; }
}

/// <summary>
/// Response from POST /v2/swap/chain/{id}/refund/ark — Boltz-signed PSBTs.
/// </summary>
public class ChainArkRefundResponse
{
    [JsonPropertyName("transaction")]
    public required string Transaction { get; set; }

    [JsonPropertyName("checkpoint")]
    public required string Checkpoint { get; set; }
}
