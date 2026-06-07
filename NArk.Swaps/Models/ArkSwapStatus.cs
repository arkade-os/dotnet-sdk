namespace NArk.Swaps.Models;

public enum ArkSwapStatus
{
    Pending,
    Settled,
    Failed,
    Recoverable,
    Refunded,
    Unknown
}