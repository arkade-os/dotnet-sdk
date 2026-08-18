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
/// The exact coins to spend, when the policy picked them — the settlement counterpart of
/// the coins an <c>ISweepPolicy</c> yields. <see langword="null"/> leaves coin selection
/// to the settlement rail.
/// </param>
/// <param name="Reference">Optional correlation id carried into <see cref="SettlementRequest.Reference"/>.</param>
public record SettlementPlan(
    SettlementDestination Destination,
    long AmountSats,
    IReadOnlyCollection<ArkCoin>? Coins = null,
    string? Reference = null);
