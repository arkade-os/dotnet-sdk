using NArk.Swaps.Models;

namespace NArk.Swaps.Abstractions;

public record SwapStatusChangedEvent(
    string SwapId,
    string WalletId,
    string ProviderId,
    ArkSwapStatus NewStatus,
    string? FailReason = null);
