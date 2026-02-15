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
    /// Flexible key-value metadata for swap-type-specific data.
    /// Chain swaps store preimage, ephemeral key, Boltz response, BTC address, etc.
    /// </summary>
    public Dictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// Well-known metadata keys for chain swaps.
/// </summary>
public static class SwapMetadata
{
    public const string Preimage = "preimage";
    public const string EphemeralKey = "ephemeralKey";
    public const string BoltzResponse = "boltzResponse";
    public const string BtcAddress = "btcAddress";
    public const string CrossSigned = "crossSigned";
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

    public static string? Get(this ArkSwap swap, string key)
    {
        return swap.Metadata?.TryGetValue(key, out var value) == true ? value : null;
    }
}
