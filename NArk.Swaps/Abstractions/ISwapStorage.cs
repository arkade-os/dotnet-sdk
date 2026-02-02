using NArk.Swaps.Models;

namespace NArk.Swaps.Abstractions;

public interface ISwapStorage
{
    public event EventHandler<ArkSwap>? SwapsChanged;

    /// <summary>
    /// Save or update a swap.
    /// </summary>
    Task SaveSwap(string walletId, ArkSwap swap, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get swaps with comprehensive filtering.
    /// </summary>
    /// <param name="walletId">Filter by wallet ID (recommended for user-facing queries)</param>
    /// <param name="swapIds">Filter by specific swap IDs</param>
    /// <param name="active">Filter by active status (Pending or Unknown). Null returns all.</param>
    /// <param name="swapType">Filter by swap type (ReverseSubmarine or Submarine)</param>
    /// <param name="status">Filter by specific status</param>
    /// <param name="contractScripts">Filter by contract scripts</param>
    /// <param name="hash">Filter by payment hash</param>
    /// <param name="invoice">Filter by invoice</param>
    /// <param name="searchText">Search text across SwapId, Invoice, and Hash fields</param>
    /// <param name="skip">Number of records to skip for pagination</param>
    /// <param name="take">Number of records to take for pagination</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<IReadOnlyCollection<ArkSwap>> GetSwaps(
        string? walletId = null,
        string[]? swapIds = null,
        bool? active = null,
        ArkSwapType? swapType = null,
        ArkSwapStatus? status = null,
        string[]? contractScripts = null,
        string? hash = null,
        string? invoice = null,
        string? searchText = null,
        int? skip = null,
        int? take = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Update the status of a swap.
    /// </summary>
    Task<bool> UpdateSwapStatus(
        string walletId,
        string swapId,
        ArkSwapStatus status,
        string? failReason = null,
        CancellationToken cancellationToken = default);
}
