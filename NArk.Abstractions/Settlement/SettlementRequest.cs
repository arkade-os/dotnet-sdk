namespace NArk.Abstractions.Settlement;

/// <summary>
/// A single settlement execution: move <paramref name="Amount"/> of
/// <paramref name="SourceAsset"/> out of <paramref name="WalletId"/> to
/// <paramref name="Destination"/>.
/// </summary>
/// <param name="WalletId">The wallet the funds leave.</param>
/// <param name="Amount">
/// Amount to settle, in <paramref name="SourceAsset"/>'s atomic units — satoshis for BTC.
/// Must be positive.
/// </param>
/// <param name="Destination">Where the funds go.</param>
/// <param name="SourceAsset">
/// What leaves the wallet: <see cref="SettlementAssets.Btc"/> (the default) or the id of an
/// Arkade-issued asset. A rail that only moves value keeps the destination asset equal to
/// this; a rail that converts (a swap, an exchange) reads both sides and reports what
/// arrived in <see cref="SettlementResult.DestinationAtomicAmount"/>.
/// </param>
/// <param name="Coins">
/// The exact coins to spend. Supplied when a policy already picked them (the sweep path);
/// <see langword="null"/> leaves coin selection to the settlement implementation.
/// </param>
/// <param name="Reference">
/// Optional caller-supplied correlation id (a store id, an invoice id) carried through
/// logs and <c>PostSettlementActionEvent</c>. The SDK never interprets it.
/// </param>
public record SettlementRequest(
    string WalletId,
    long Amount,
    SettlementDestination Destination,
    string SourceAsset = SettlementAssets.Btc,
    IReadOnlyCollection<ArkCoin>? Coins = null,
    string? Reference = null)
{
    /// <summary>True when this settlement moves BTC rather than an Arkade-issued asset.</summary>
    public bool IsBtc => SourceAsset.Equals(SettlementAssets.Btc, StringComparison.OrdinalIgnoreCase);
}
