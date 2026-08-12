using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Settlement;

namespace NArk.Core.Settlement;

/// <summary>
/// The default settlement policy: yields a settlement for every enabled rule in
/// <see cref="ISettlementConfigProvider"/> whose threshold the wallet's available
/// balance has reached, lowest threshold first.
/// <para>
/// The threshold gates <em>when</em> a settlement happens, never <em>how much</em> moves —
/// a wallet configured at 100 000 sats that reaches 250 000 settles all 250 000. Cap a
/// single settlement with <see cref="SettlementConfig.MaxAmountSats"/> when a rail has
/// an upper limit; with a cap in place a second matching rule can settle the remainder,
/// since the engine executes plans against a shrinking balance.
/// </para>
/// </summary>
public class BalanceThresholdSettlementPolicy(
    ISettlementConfigProvider configProvider,
    ILogger<BalanceThresholdSettlementPolicy>? logger = null) : ISettlementPolicy
{
    /// <inheritdoc />
    public async IAsyncEnumerable<SettlementPlan> EvaluateAsync(
        SettlementContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (context.AvailableBalanceSats <= 0)
            yield break;

        var configs = await configProvider.GetConfigs(context.WalletId, cancellationToken);

        // Providers are free to ignore the walletId filter, so filter again here.
        var candidates = configs
            .Where(config => config.Enabled && config.WalletId == context.WalletId)
            .Where(config => context.AvailableBalanceSats >= config.ThresholdSats)
            .OrderBy(config => config.ThresholdSats);

        foreach (var config in candidates)
        {
            var amount = config.MaxAmountSats is { } max
                ? Math.Min(context.AvailableBalanceSats, max)
                : context.AvailableBalanceSats;

            if (amount <= 0)
            {
                logger?.LogWarning(
                    "Wallet {WalletId} matched a settlement rule with a non-positive cap {MaxAmountSats}; skipping",
                    context.WalletId, config.MaxAmountSats);
                continue;
            }

            logger?.LogDebug(
                "Wallet {WalletId} balance {BalanceSats} sats reached threshold {ThresholdSats} sats; planning settlement of {AmountSats} sats to {Network}/{Asset}",
                context.WalletId, context.AvailableBalanceSats, config.ThresholdSats, amount,
                config.Destination.Network, config.Destination.Asset);

            yield return new SettlementPlan(config.Destination, amount);
        }
    }
}
