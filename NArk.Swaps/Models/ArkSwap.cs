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

    /// <summary>
    /// Arkade address (string form) of the refund destination contract. Set the first
    /// time a cooperative refund derives a destination so subsequent poll retries
    /// reuse it instead of deriving fresh contracts and leaking orphan rows into
    /// <c>IContractStorage</c>.
    /// </summary>
    public const string RefundDestination = "refundDestination";

    // ── Persistence shim for the ProviderId / Route fields on ArkSwap.
    // These properties don't have dedicated columns on ArkSwapEntity (yet —
    // see issue #79 review), so EfCoreSwapStorage round-trips them through
    // the existing Metadata jsonb column under these well-known keys. Having
    // them as constants keeps the serialization symmetric and reviewable.
    public const string ProviderId = "providerId";
    public const string RouteSourceNetwork = "route.source.network";
    public const string RouteSourceAssetId = "route.source.assetId";
    public const string RouteDestinationNetwork = "route.destination.network";
    public const string RouteDestinationAssetId = "route.destination.assetId";
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
