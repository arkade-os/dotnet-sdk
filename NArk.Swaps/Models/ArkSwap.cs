using NArk.Abstractions.Contracts;
using NArk.Swaps.Abstractions;

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
    public SwapRoute? Route { get; init; }
    public string? ProviderId { get; init; }
}

