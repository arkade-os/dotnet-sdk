namespace NArk.Swaps.Models;

public record ArkSwap(
    string SwapId,
    string WalletId,
    ArkSwapType SwapType,
    string Invoice,
    long ExpectedAmount,
    string ContractScript,
    string Address,
    ArkSwapStatus Status,
    string? FailReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Hash);

public enum ArkSwapStatus
{
    Pending,
    Settled,
    Failed,
    Refunded,
    Unknown
}

public enum ArkSwapType
{
    ReverseSubmarine,
    Submarine
}

public static class SwapExtensions
{
    public static bool IsActive(this ArkSwapStatus swapStatus)
    {
        return swapStatus is ArkSwapStatus.Pending or ArkSwapStatus.Unknown;
    }

}