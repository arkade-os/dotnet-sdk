using NArk.Swaps.Models;

namespace NArk.Swaps.Abstractions;

public interface ISwapStorage
{
    public event EventHandler<ArkSwap>? SwapsChanged;
    Task SaveSwap(string walletId, ArkSwap swap, CancellationToken cancellationToken = default);
    Task<ArkSwap> GetSwap(string swapId, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<ArkSwap>> GetActiveSwaps(string? walletId = null, CancellationToken cancellationToken = default);
}