namespace NArk.Abstractions.Payments;

/// <summary>
/// Persistence for inbound payment requests.
/// </summary>
public interface IPaymentRequestStorage
{
    event EventHandler<ArkPaymentRequest>? PaymentRequestChanged;

    /// <summary>
    /// Save or update a payment request.
    /// </summary>
    Task SavePaymentRequest(ArkPaymentRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Query payment requests with explicit filter parameters.
    /// </summary>
    /// <param name="walletIds">Filter by wallet IDs. If null, all wallets.</param>
    /// <param name="requestIds">Filter by request IDs. If null, no filter.</param>
    /// <param name="statuses">Filter by status. If null, all statuses.</param>
    /// <param name="searchText">Search text across RequestId, Description. If null, no text search.</param>
    /// <param name="skip">Number of records to skip for pagination. If null, no skip.</param>
    /// <param name="take">Number of records to take for pagination. If null, no limit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyCollection<ArkPaymentRequest>> GetPaymentRequests(
        string[]? walletIds = null,
        string[]? requestIds = null,
        ArkPaymentRequestStatus[]? statuses = null,
        string? searchText = null,
        int? skip = null,
        int? take = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Find a payment request by one of its watched contract scripts.
    /// This is the primary lookup for matching incoming VTXOs to requests.
    /// </summary>
    Task<ArkPaymentRequest?> GetPaymentRequestByScript(
        string script,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Update the status, received amount, overpayment, and received assets of a payment request.
    /// </summary>
    Task<bool> UpdatePaymentRequestStatus(
        string walletId,
        string requestId,
        ArkPaymentRequestStatus status,
        ulong receivedAmount,
        ulong overpayment = 0,
        IReadOnlyList<VTXOs.VtxoAsset>? receivedAssets = null,
        CancellationToken cancellationToken = default);
}
