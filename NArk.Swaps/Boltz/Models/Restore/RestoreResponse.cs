using System.Text.Json.Serialization;

namespace NArk.Swaps.Boltz.Models.Restore;

/// <summary>
/// Reference to the on-chain lockup transaction for a UTXO-based swap leg.
/// </summary>
public record Transaction
{
    /// <summary>
    /// ID of the lockup transaction.
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// Index of the lockup output in the transaction.
    /// </summary>
    [JsonPropertyName("vout")]
    public int Vout { get; init; }
}

/// <summary>
/// Reference to the lockup transaction for an EVM-based swap leg.
/// </summary>
public record EvmTransaction
{
    /// <summary>
    /// Hash of the EVM lockup transaction.
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }
}

/// <summary>
/// Polymorphic claim/refund details for one leg of a restorable swap, discriminated by
/// <c>type</c>: <see cref="UtxoSwapDetails"/> ("utxo") for tapscript-tree-based chains (BTC,
/// Liquid, or the Ark leg of any swap), <see cref="EvmSwapDetails"/> ("evm") for EVM chains.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(UtxoSwapDetails), "utxo")]
[JsonDerivedType(typeof(EvmSwapDetails), "evm")]
public abstract record SwapDetails
{
    /// <summary>
    /// Amount locked in the swap (in satoshis), if available.
    /// </summary>
    [JsonPropertyName("amount")]
    public long? Amount { get; init; }

    /// <summary>
    /// Block height at which this leg's HTLC/lockup times out.
    /// </summary>
    [JsonPropertyName("timeoutBlockHeight")]
    public long TimeoutBlockHeight { get; init; }
}

/// <summary>
/// Tapscript-tree-based claim/refund details — used for BTC, Liquid, and the Ark leg of any
/// swap (submarine, reverse, or chain).
/// </summary>
public record UtxoSwapDetails : SwapDetails
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
    /// The lockup address for this swap leg.
    /// </summary>
    [JsonPropertyName("lockupAddress")]
    public required string LockupAddress { get; init; }

    /// <summary>
    /// The server's public key used in the swap.
    /// </summary>
    [JsonPropertyName("serverPublicKey")]
    public required string ServerPublicKey { get; init; }

    /// <summary>
    /// The lockup transaction, if known.
    /// </summary>
    [JsonPropertyName("transaction")]
    public Transaction? Transaction { get; init; }

    /// <summary>
    /// Blinding key for Liquid swaps (optional).
    /// </summary>
    [JsonPropertyName("blindingKey")]
    public string? BlindingKey { get; init; }
}

/// <summary>
/// EVM-chain claim/refund details. The full lockup parameters (amount, refund/claim address,
/// exact timelock) are reconstructed from the contract's <c>Lockup</c> event filtered by
/// preimage hash rather than carried in this payload — <see cref="SwapDetails.TimeoutBlockHeight"/>
/// and <see cref="SwapDetails.Amount"/> here are informational only.
/// </summary>
public record EvmSwapDetails : SwapDetails
{
    /// <summary>
    /// Address of the EtherSwap/ERC20Swap contract the funds are locked in.
    /// </summary>
    [JsonPropertyName("contractAddress")]
    public required string ContractAddress { get; init; }

    /// <summary>
    /// EVM address allowed to claim the swap, if known.
    /// </summary>
    [JsonPropertyName("claimAddress")]
    public string? ClaimAddress { get; init; }

    /// <summary>
    /// The lockup transaction, if known.
    /// </summary>
    [JsonPropertyName("transaction")]
    public EvmTransaction? Transaction { get; init; }
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
    /// Asset the client is supposed to send (e.g., "BTC", "ARK").
    /// </summary>
    [JsonPropertyName("from")]
    public required string From { get; init; }

    /// <summary>
    /// Asset the client is supposed to receive (e.g., "BTC", "ARK").
    /// </summary>
    [JsonPropertyName("to")]
    public required string To { get; init; }

    /// <summary>
    /// Preimage hash (SHA256) required to claim the swap.
    /// </summary>
    [JsonPropertyName("preimageHash")]
    public string? PreimageHash { get; init; }

    /// <summary>
    /// Details to claim the swap — the side we can claim with the preimage. Only set for
    /// reverse swaps (Ark leg) and chain swaps (whichever leg we don't lock ourselves).
    /// </summary>
    [JsonPropertyName("claimDetails")]
    public SwapDetails? ClaimDetails { get; init; }

    /// <summary>
    /// Details to refund the swap — the side we locked ourselves. Only set for submarine swaps
    /// (Ark leg) and chain swaps (whichever leg we lock ourselves).
    /// </summary>
    [JsonPropertyName("refundDetails")]
    public SwapDetails? RefundDetails { get; init; }

    /// <summary>
    /// Gets the relevant swap details (claimDetails for reverse, refundDetails for submarine).
    /// Not meaningful for chain swaps, which populate both — use <see cref="ClaimDetails"/>/
    /// <see cref="RefundDetails"/> directly there.
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

    /// <summary>
    /// Returns true if this is a chain swap (both legs are on-chain, neither is Lightning).
    /// </summary>
    [JsonIgnore]
    public bool IsChainSwap => Type == "chain";
}
