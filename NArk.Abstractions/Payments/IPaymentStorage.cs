namespace NArk.Abstractions.Payments;

/// <summary>
/// Persistence for outbound payments.
/// </summary>
public interface IPaymentStorage
{
#pragma warning disable CS1591
    event EventHandler<ArkPayment>? PaymentChanged;
#pragma warning restore CS1591

    /// <summary>
    /// Save or update a payment.
    /// </summary>
    Task SavePayment(ArkPayment payment, CancellationToken cancellationToken = default);

    /// <summary>
    /// Query payments with explicit filter parameters.
    /// </summary>
    /// <param name="walletIds">Filter by wallet IDs. If null, all wallets.</param>
    /// <param name="paymentIds">Filter by payment IDs. If null, no filter.</param>
    /// <param name="statuses">Filter by status. If null, all statuses.</param>
    /// <param name="methods">Filter by payment method. If null, all methods.</param>
    /// <param name="intentTxIds">Filter by linked intent tx IDs. If null, no filter.</param>
    /// <param name="swapIds">Filter by linked swap IDs. If null, no filter.</param>
    /// <param name="searchText">Search text across PaymentId, Recipient. If null, no text search.</param>
    /// <param name="skip">Number of records to skip for pagination. If null, no skip.</param>
    /// <param name="take">Number of records to take for pagination. If null, no limit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyCollection<ArkPayment>> GetPayments(
        string[]? walletIds = null,
        string[]? paymentIds = null,
        ArkPaymentStatus[]? statuses = null,
        ArkPaymentMethod[]? methods = null,
        string[]? intentTxIds = null,
        string[]? swapIds = null,
        string? searchText = null,
        int? skip = null,
        int? take = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Update the status of a payment.
    /// </summary>
    Task<bool> UpdatePaymentStatus(
        string walletId,
        string paymentId,
        ArkPaymentStatus status,
        string? failReason = null,
        string? onchainTxId = null,
        CancellationToken cancellationToken = default);
}
