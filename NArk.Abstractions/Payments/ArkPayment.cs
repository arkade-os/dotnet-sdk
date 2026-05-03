using NArk.Abstractions.VTXOs;

namespace NArk.Abstractions.Payments;

/// <summary>
/// Tracks an outbound payment — a send from this wallet to a recipient.
/// Links to the underlying protocol object (intent, swap, or on-chain tx) as proof.
/// </summary>
public record ArkPayment(
    string PaymentId,
    string WalletId,
    string Recipient,
    ulong Amount,
    ArkPaymentMethod Method,
    ArkPaymentStatus Status,
    string? FailReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt)
{
    /// <summary>
    /// Intent transaction ID — set when method is ArkSend.
    /// </summary>
    public string? IntentTxId { get; init; }

    /// <summary>
    /// Swap ID — set when method is SubmarineSwap or ChainSwap.
    /// </summary>
    public string? SwapId { get; init; }

    /// <summary>
    /// On-chain transaction ID — set when method is CollaborativeExit.
    /// </summary>
    public string? OnchainTxId { get; init; }

    /// <summary>
    /// Assets transferred with this payment. Null for BTC-only payments.
    /// </summary>
    public IReadOnlyList<VtxoAsset>? Assets { get; init; }

    /// <summary>
    /// Application-level metadata.
    /// </summary>
    public Dictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// How the payment was or will be delivered.
/// </summary>
public enum ArkPaymentMethod
{
    /// <summary>Direct Ark protocol send (intent → batch round).</summary>
    ArkSend,
    /// <summary>Collaborative exit to an on-chain address.</summary>
    CollaborativeExit,
    /// <summary>Submarine swap (Ark → Lightning).</summary>
    SubmarineSwap,
    /// <summary>Chain swap (Ark → BTC on-chain or BTC → Ark).</summary>
    ChainSwap
}

/// <summary>
/// Lifecycle of an outbound payment.
/// </summary>
public enum ArkPaymentStatus
{
    Pending,
    Completed,
    Failed,
    /// <summary>Payment was explicitly cancelled by the user or application.</summary>
    Cancelled
}
