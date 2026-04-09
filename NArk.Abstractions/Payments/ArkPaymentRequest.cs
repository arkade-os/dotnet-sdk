using NArk.Abstractions.VTXOs;

namespace NArk.Abstractions.Payments;

/// <summary>
/// Tracks an inbound payment request — a request for someone to pay this wallet.
/// Supports multiple payment options (Ark address, boarding address, Lightning invoice).
/// </summary>
public record ArkPaymentRequest(
    string RequestId,
    string WalletId,
    ulong? Amount,
    string? Description,
    ArkPaymentRequestStatus Status,
    ulong ReceivedAmount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt)
{
    /// <summary>
    /// Ark protocol address for off-chain payment.
    /// </summary>
    public string? ArkAddress { get; init; }

    /// <summary>
    /// On-chain boarding address (P2TR taproot).
    /// </summary>
    public string? BoardingAddress { get; init; }

    /// <summary>
    /// BOLT11 Lightning invoice (from reverse submarine swap).
    /// </summary>
    public string? LightningInvoice { get; init; }

    /// <summary>
    /// Contract scripts being watched for incoming VTXOs.
    /// Used internally to match VTXOs to this request.
    /// </summary>
    public string[] ContractScripts { get; init; } = [];

    /// <summary>
    /// Swap ID for the reverse submarine swap, if Lightning is an option.
    /// </summary>
    public string? SwapId { get; init; }

    /// <summary>
    /// Sats received beyond the requested amount. Zero when Amount is null or not yet overpaid.
    /// </summary>
    public ulong Overpayment { get; init; }

    /// <summary>
    /// Expected asset for this payment request. Null for BTC-only requests.
    /// </summary>
    public VtxoAsset? ExpectedAsset { get; init; }

    /// <summary>
    /// Assets received so far. Null when no assets received yet.
    /// </summary>
    public IReadOnlyList<VtxoAsset>? ReceivedAssets { get; init; }

    /// <summary>
    /// Application-level metadata.
    /// </summary>
    public Dictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// Lifecycle of an inbound payment request.
/// </summary>
public enum ArkPaymentRequestStatus
{
    Pending,
    PartiallyPaid,
    Paid,
    Expired,
    /// <summary>Request was explicitly cancelled by the user or application.</summary>
    Cancelled
}
