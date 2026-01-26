using System.Text.Json.Serialization;

namespace NArk.Swaps.Boltz.Models.Restore;

/// <summary>
/// Represents a tapscript tree leaf with version and script output.
/// </summary>
public record SwapTreeLeaf
{
    /// <summary>
    /// Tapscript version identifier.
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; init; }

    /// <summary>
    /// Script encoded as HEX.
    /// </summary>
    [JsonPropertyName("output")]
    public required string Output { get; init; }
}

/// <summary>
/// Represents the tapscript tree structure for a swap.
/// Contains various leaf nodes for different spending paths.
/// </summary>
public record SwapTree
{
    /// <summary>
    /// Claim leaf - standard claim path requiring preimage + receiver signature.
    /// </summary>
    [JsonPropertyName("claimLeaf")]
    public SwapTreeLeaf? ClaimLeaf { get; init; }

    /// <summary>
    /// Refund leaf - standard refund path.
    /// </summary>
    [JsonPropertyName("refundLeaf")]
    public SwapTreeLeaf? RefundLeaf { get; init; }

    /// <summary>
    /// Covenant claim leaf - cooperative claim with server.
    /// </summary>
    [JsonPropertyName("covenantClaimLeaf")]
    public SwapTreeLeaf? CovenantClaimLeaf { get; init; }

    /// <summary>
    /// Refund without Boltz leaf - refund after timeout without Boltz cooperation.
    /// </summary>
    [JsonPropertyName("refundWithoutBoltzLeaf")]
    public SwapTreeLeaf? RefundWithoutBoltzLeaf { get; init; }

    /// <summary>
    /// Unilateral claim leaf - claim after delay without server cooperation.
    /// </summary>
    [JsonPropertyName("unilateralClaimLeaf")]
    public SwapTreeLeaf? UnilateralClaimLeaf { get; init; }

    /// <summary>
    /// Unilateral refund leaf - refund after delay without server cooperation.
    /// </summary>
    [JsonPropertyName("unilateralRefundLeaf")]
    public SwapTreeLeaf? UnilateralRefundLeaf { get; init; }

    /// <summary>
    /// Unilateral refund without Boltz leaf - refund after longer delay.
    /// </summary>
    [JsonPropertyName("unilateralRefundWithoutBoltzLeaf")]
    public SwapTreeLeaf? UnilateralRefundWithoutBoltzLeaf { get; init; }
}
