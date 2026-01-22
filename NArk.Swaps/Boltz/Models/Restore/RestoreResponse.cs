using System.Text.Json.Serialization;

namespace NArk.Swaps.Boltz.Models.Restore;

/// <summary>
/// Details about a restorable swap including tree structure and timeouts.
/// </summary>
public record SwapDetails
{
    /// <summary>
    /// The tapscript tree containing all spending paths.
    /// </summary>
    [JsonPropertyName("tree")]
    public required SwapTree Tree { get; init; }

    /// <summary>
    /// Index of the key in the derivation path (for XPUB restoration).
    /// </summary>
    [JsonPropertyName("keyIndex")]
    public int? KeyIndex { get; init; }

    /// <summary>
    /// The lockup address for this swap.
    /// </summary>
    [JsonPropertyName("lockupAddress")]
    public required string LockupAddress { get; init; }

    /// <summary>
    /// The server's public key used in the swap.
    /// </summary>
    [JsonPropertyName("serverPublicKey")]
    public required string ServerPublicKey { get; init; }

    /// <summary>
    /// Block height at which the swap times out.
    /// </summary>
    [JsonPropertyName("timeoutBlockHeight")]
    public required long TimeoutBlockHeight { get; init; }

    /// <summary>
    /// Amount in satoshis (if available from transaction).
    /// </summary>
    [JsonPropertyName("amount")]
    public long? Amount { get; init; }

    /// <summary>
    /// Transaction hex (if available).
    /// </summary>
    [JsonPropertyName("transaction")]
    public string? Transaction { get; init; }

    /// <summary>
    /// Blinding key for Liquid swaps (optional).
    /// </summary>
    [JsonPropertyName("blindingKey")]
    public string? BlindingKey { get; init; }
}

/// <summary>
/// Represents a swap that can be restored from the Boltz API.
/// </summary>
public record RestorableSwap
{
    /// <summary>
    /// Unique swap identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// Swap type: "submarine", "reverse", or "chain".
    /// </summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>
    /// Current swap status.
    /// </summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>
    /// Unix timestamp when the swap was created.
    /// </summary>
    [JsonPropertyName("createdAt")]
    public required long CreatedAt { get; init; }

    /// <summary>
    /// Source asset (e.g., "BTC", "ARK").
    /// </summary>
    [JsonPropertyName("from")]
    public required string From { get; init; }

    /// <summary>
    /// Destination asset (e.g., "BTC", "ARK").
    /// </summary>
    [JsonPropertyName("to")]
    public required string To { get; init; }

    /// <summary>
    /// Preimage hash (SHA256) required to claim the swap.
    /// </summary>
    [JsonPropertyName("preimageHash")]
    public string? PreimageHash { get; init; }

    /// <summary>
    /// Claim details for reverse swaps (receiving Lightning, sending on-chain).
    /// </summary>
    [JsonPropertyName("claimDetails")]
    public SwapDetails? ClaimDetails { get; init; }

    /// <summary>
    /// Refund details for submarine swaps (sending on-chain, receiving Lightning).
    /// </summary>
    [JsonPropertyName("refundDetails")]
    public SwapDetails? RefundDetails { get; init; }

    /// <summary>
    /// Gets the relevant swap details (claimDetails for reverse, refundDetails for submarine).
    /// </summary>
    [JsonIgnore]
    public SwapDetails? Details => ClaimDetails ?? RefundDetails;

    /// <summary>
    /// Returns true if this is a reverse swap (receiving Lightning).
    /// </summary>
    [JsonIgnore]
    public bool IsReverseSwap => Type == "reverse" && ClaimDetails != null;

    /// <summary>
    /// Returns true if this is a submarine swap (sending on-chain).
    /// </summary>
    [JsonIgnore]
    public bool IsSubmarineSwap => Type == "submarine" && RefundDetails != null;
}
