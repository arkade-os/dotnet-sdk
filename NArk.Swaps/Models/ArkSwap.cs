using NArk.Abstractions.Contracts;

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
    string Hash)
{
    /// <summary>
    /// Preimage hex for chain swaps (needed for claiming).
    /// </summary>
    public string? Preimage { get; init; }

    /// <summary>
    /// Ephemeral EC private key hex for BTC-side MuSig2 operations in chain swaps.
    /// </summary>
    public string? EphemeralKeyHex { get; init; }

    /// <summary>
    /// Serialized Boltz response JSON for recovery/debugging.
    /// </summary>
    public string? BoltzResponseJson { get; init; }

    /// <summary>
    /// BTC lockup address for chain swaps (either user's lockup or server's lockup).
    /// </summary>
    public string? BtcAddress { get; init; }
}

/// <summary>
/// A swap with its associated contract entity.
/// </summary>
public record ArkSwapWithContract(
    ArkSwap Swap,
    ArkContractEntity? Contract);

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
    Submarine,
    ChainBtcToArk,
    ChainArkToBtc
}

public static class SwapExtensions
{
    public static bool IsActive(this ArkSwapStatus swapStatus)
    {
        return swapStatus is ArkSwapStatus.Pending or ArkSwapStatus.Unknown;
    }

}