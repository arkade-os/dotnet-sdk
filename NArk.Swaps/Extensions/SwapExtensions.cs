using NArk.Swaps.Models;

namespace NArk.Swaps.Extensions;

public static class SwapExtensions
{
    public static bool IsActive(this ArkSwapStatus swapStatus)
    {
        return swapStatus is ArkSwapStatus.Pending or ArkSwapStatus.Unknown;
    }

    public static string? Get(this ArkSwap swap, string key)
    {
        return swap.Metadata?.TryGetValue(key, out var value) == true ? value : null;
    }

    public static bool IsTerminalState(this ArkSwapStatus status)
    {
        return status is ArkSwapStatus.Refunded or ArkSwapStatus.Settled or ArkSwapStatus.Failed;
    }

    public static bool IsSuccess(this ArkSwapStatus status)
    {
        return status is ArkSwapStatus.Settled or ArkSwapStatus.Refunded;
    }
}