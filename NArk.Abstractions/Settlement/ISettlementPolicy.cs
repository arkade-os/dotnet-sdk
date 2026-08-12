using NArk.Abstractions.Blockchain;

namespace NArk.Abstractions.Settlement;

/// <summary>
/// Decides <em>whether</em> a wallet should settle right now, and <em>how much</em>.
/// The policy never moves funds — it returns a <see cref="SettlementPlan"/> that the
/// settlement engine routes to an <see cref="ISettlementService"/>.
/// <para>
/// Register several policies to combine strategies (a balance threshold, a scheduled
/// payout, an expiry-driven sweep); the engine takes the first non-null plan in
/// <see cref="SettlementPlan.Priority"/> order.
/// </para>
/// </summary>
public interface ISettlementPolicy
{
    /// <summary>
    /// Returns a plan when this policy wants the wallet settled, or <see langword="null"/>
    /// to stand down. Must not have side effects — it runs on every wallet activity tick.
    /// </summary>
    Task<SettlementPlan?> EvaluateAsync(SettlementContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// The wallet state a policy evaluates against: everything that is spendable right now,
/// with coins locked by pending intents and coins past their expiry already removed.
/// </summary>
/// <param name="WalletId">The wallet under evaluation.</param>
/// <param name="AvailableCoins">Coins that could fund a settlement.</param>
/// <param name="AvailableBalanceSats">Sum of <paramref name="AvailableCoins"/>, in satoshis.</param>
/// <param name="ChainTime">Chain time and height the availability was computed against.</param>
public record SettlementContext(
    string WalletId,
    IReadOnlyCollection<ArkCoin> AvailableCoins,
    long AvailableBalanceSats,
    TimeHeight ChainTime);

/// <summary>
/// A policy's decision: settle <paramref name="AmountSats"/> to <paramref name="Destination"/>.
/// </summary>
/// <param name="Destination">Where the funds should go.</param>
/// <param name="AmountSats">How much to move, in satoshis.</param>
/// <param name="Coins">
/// The exact coins to spend, when the policy picked them. <see langword="null"/> leaves
/// coin selection to the settlement rail.
/// </param>
/// <param name="Priority">
/// Lower runs first when several policies produce a plan. Defaults to 0.
/// </param>
/// <param name="Reference">Optional correlation id carried into <see cref="SettlementRequest.Reference"/>.</param>
public record SettlementPlan(
    SettlementDestination Destination,
    long AmountSats,
    IReadOnlyCollection<ArkCoin>? Coins = null,
    int Priority = 0,
    string? Reference = null);
