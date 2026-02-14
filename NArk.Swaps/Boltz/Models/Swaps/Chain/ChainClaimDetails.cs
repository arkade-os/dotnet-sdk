using System.Text.Json.Serialization;

namespace NArk.Swaps.Boltz.Models.Swaps.Chain;

/// <summary>
/// Response from GET /v2/swap/chain/{id}/claim — Boltz's signing details for cooperative claim.
/// </summary>
public class ChainClaimDetails
{
    /// <summary>
    /// Boltz's MuSig2 public nonce (hex).
    /// </summary>
    [JsonPropertyName("pubNonce")]
    public required string PubNonce { get; set; }

    /// <summary>
    /// Boltz's public key used in the MuSig2 aggregate (hex).
    /// </summary>
    [JsonPropertyName("publicKey")]
    public required string PublicKey { get; set; }

    /// <summary>
    /// Hash of Boltz's claim transaction that we need to cross-sign (hex).
    /// </summary>
    [JsonPropertyName("transactionHash")]
    public required string TransactionHash { get; set; }
}
