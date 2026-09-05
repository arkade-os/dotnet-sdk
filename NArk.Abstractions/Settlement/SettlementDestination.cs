namespace NArk.Abstractions.Settlement;

/// <summary>
/// Where a settlement moves funds to: a network, an asset on that network, and an
/// address on it.
/// <para>
/// Network and asset are free-form strings rather than enums on purpose — an
/// application can settle to a network the SDK has never heard of (a stablecoin
/// chain, an exchange deposit rail) by registering its own
/// <see cref="ISettlementService"/> for it, with no change to the SDK.
/// </para>
/// </summary>
/// <param name="Network">Network identifier, compared case-insensitively. See <see cref="SettlementNetworks"/>.</param>
/// <param name="Asset">Asset identifier on that network, compared case-insensitively. See <see cref="SettlementAssets"/>.</param>
/// <param name="Address">
/// Destination address. <see langword="null"/> means "back to the settling wallet itself",
/// which only the Arkade network supports.
/// </param>
public record SettlementDestination(string Network, string Asset, string? Address)
{
    /// <summary>An Arkade address (<c>ark1…</c> / <c>tark1…</c>) receiving BTC.</summary>
    public static SettlementDestination Ark(string address) =>
        new(SettlementNetworks.Ark, SettlementAssets.Btc, address);

    /// <summary>
    /// The settling wallet itself: funds are consolidated into a freshly derived
    /// Arkade address owned by the same wallet.
    /// </summary>
    public static SettlementDestination ArkSelf() =>
        new(SettlementNetworks.Ark, SettlementAssets.Btc, null);

    /// <summary>An Arkade address receiving an Arkade-issued asset.</summary>
    /// <param name="address">Arkade address of the recipient.</param>
    /// <param name="assetId">Identifier of the Arkade-issued asset.</param>
    public static SettlementDestination ArkAsset(string address, string assetId) =>
        new(SettlementNetworks.Ark, assetId, address);

    /// <summary>An on-chain Bitcoin address receiving BTC.</summary>
    public static SettlementDestination BitcoinOnchain(string address) =>
        new(SettlementNetworks.Bitcoin, SettlementAssets.Btc, address);

    /// <summary>True when this destination is on <paramref name="network"/>, ignoring case.</summary>
    public bool IsNetwork(string network) =>
        Network.Equals(network, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when this destination's asset is <paramref name="asset"/>, ignoring case.</summary>
    public bool IsAsset(string asset) =>
        Asset.Equals(asset, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when this destination is <paramref name="asset"/> on <paramref name="network"/>, ignoring case.</summary>
    public bool Is(string network, string asset) =>
        IsNetwork(network) && IsAsset(asset);
}

/// <summary>
/// Network identifiers the SDK itself settles to. Any other value is a rail an
/// application registers — an EVM chain, a stablecoin network, an exchange deposit —
/// and needs no entry here.
/// </summary>
public static class SettlementNetworks
{
    /// <summary>The Arkade network — an off-chain VTXO transfer.</summary>
    public const string Ark = "ark";

    /// <summary>The Bitcoin main chain — settled by a collaborative exit.</summary>
    public const string Bitcoin = "bitcoin";
}

/// <summary>Asset identifiers the SDK itself settles.</summary>
public static class SettlementAssets
{
    /// <summary>Bitcoin, denominated in satoshis.</summary>
    public const string Btc = "BTC";
}
