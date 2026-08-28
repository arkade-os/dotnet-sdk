using NArk.Abstractions.Blockchain;

namespace NArk.Abstractions.Settlement;

/// <summary>
/// Decides <em>whether</em> a wallet should settle right now, and <em>how much</em>.
/// The policy never moves funds — it yields <see cref="SettlementPlan"/>s that the
/// settlement engine routes to an <see cref="ISettlementService"/>.
/// <para>
/// This mirrors <c>ISweepPolicy</c>: register several policies to combine strategies
/// (a balance threshold, a scheduled payout, an expiry-driven sweep) and the engine
/// executes the union of what they yield, rather than picking one winner. A policy
/// may yield nothing, one plan, or several — settling a balance across two
/// destinations is just two yields.
/// </para>
/// <para>
/// Plans are executed in the order they are yielded, policy by policy, against a
/// balance that shrinks as each one is committed; a plan that no longer fits the
/// remaining balance is skipped.
/// </para>
/// </summary>
public interface ISettlementPolicy
{
    /// <summary>
    /// Yields the settlements this policy wants for the wallet, or nothing to stand
    /// down. Must not have side effects — it runs on every wallet activity tick.
    /// </summary>
    IAsyncEnumerable<SettlementPlan> EvaluateAsync(
        SettlementContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The wallet state a policy evaluates against: everything that is spendable right now,
/// with coins locked by pending intents and coins past their expiry already removed.
/// <para>
/// BTC and Arkade-issued assets are kept apart. A coin carrying an asset holds a
/// dust-sized satoshi amount as the asset's carrier, not as spendable BTC — moving it
/// would move the asset — so such coins land in <see cref="AssetCoins"/> and
/// <see cref="AssetBalances"/>, never in <see cref="AvailableCoins"/> or
/// <see cref="AvailableBalanceSats"/>. A BTC threshold never fires on an asset carrier's
/// dust, and an asset threshold never fires on BTC.
/// </para>
/// <para>
/// Use <see cref="GetAvailableBalance"/> and <see cref="GetAvailableCoins"/> to read
/// either denomination through one call.
/// </para>
/// </summary>
/// <param name="WalletId">The wallet under evaluation.</param>
/// <param name="AvailableCoins">BTC-only coins that could fund a settlement; asset carriers are not among them.</param>
/// <param name="AvailableBalanceSats">Sum of <paramref name="AvailableCoins"/>, in satoshis.</param>
/// <param name="ChainTime">Chain time and height the availability was computed against.</param>
/// <param name="AssetCoins">Spendable coins carrying at least one Arkade-issued asset.</param>
/// <param name="AssetBalances">
/// Spendable balance per Arkade asset id, in the asset's own atomic units, summed over
/// <paramref name="AssetCoins"/>. Keyed case-insensitively.
/// </param>
public record SettlementContext(
    string WalletId,
    IReadOnlyCollection<ArkCoin> AvailableCoins,
    long AvailableBalanceSats,
    TimeHeight ChainTime,
    IReadOnlyCollection<ArkCoin> AssetCoins,
    IReadOnlyDictionary<string, ulong> AssetBalances)
{
    /// <summary>
    /// The spendable balance of <paramref name="asset"/>: satoshis for
    /// <see cref="SettlementAssets.Btc"/>, the asset's own atomic units otherwise.
    /// Returns zero for an asset the wallet does not hold.
    /// </summary>
    public long GetAvailableBalance(string asset)
    {
        if (asset.Equals(SettlementAssets.Btc, StringComparison.OrdinalIgnoreCase))
            return AvailableBalanceSats;

        return AssetBalances.TryGetValue(asset, out var amount) ? (long)amount : 0;
    }

    /// <summary>
    /// The coins that could fund a settlement of <paramref name="asset"/>: the BTC-only
    /// coins for <see cref="SettlementAssets.Btc"/>, the carriers of that asset otherwise.
    /// </summary>
    public IReadOnlyCollection<ArkCoin> GetAvailableCoins(string asset)
    {
        if (asset.Equals(SettlementAssets.Btc, StringComparison.OrdinalIgnoreCase))
            return AvailableCoins;

        return AssetCoins
            .Where(coin => coin.Assets?.Any(
                a => a.AssetId.Equals(asset, StringComparison.OrdinalIgnoreCase)) is true)
            .ToArray();
    }
}

/// <summary>
/// A policy's decision: settle <paramref name="Amount"/> of <paramref name="SourceAsset"/>
/// to <paramref name="Destination"/>.
/// </summary>
/// <param name="Destination">Where the funds should go.</param>
/// <param name="Amount">
/// How much to move, in <paramref name="SourceAsset"/>'s atomic units — satoshis for BTC.
/// </param>
/// <param name="SourceAsset">
/// What leaves the wallet: <see cref="SettlementAssets.Btc"/> (the default) or the id of an
/// Arkade-issued asset. This is the source side only; a rail that converts may deliver a
/// different asset at <paramref name="Destination"/>.
/// </param>
/// <param name="Coins">
/// The exact coins to spend, when the policy picked them — the settlement counterpart of
/// the coins an <c>ISweepPolicy</c> yields. <see langword="null"/> leaves coin selection
/// to the settlement rail.
/// </param>
/// <param name="Reference">Optional correlation id carried into <see cref="SettlementRequest.Reference"/>.</param>
public record SettlementPlan(
    SettlementDestination Destination,
    long Amount,
    string SourceAsset = SettlementAssets.Btc,
    IReadOnlyCollection<ArkCoin>? Coins = null,
    string? Reference = null);
