using Microsoft.Extensions.Logging;
using NArk.Abstractions.Settlement;

namespace NArk.Core.Settlement;

/// <summary>
/// The default settlement policy: fires when a wallet's available balance reaches the
/// threshold configured in <see cref="ISettlementConfigProvider"/>, and settles the
/// whole available balance.
/// <para>
/// The threshold gates <em>when</em> a settlement happens, never <em>how much</em> moves —
/// a wallet configured at 100 000 sats that reaches 250 000 settles all 250 000, not
/// 100 000. Cap a single settlement with <see cref="SettlementConfig.MaxAmountSats"/>
/// when a rail has an upper limit.
/// </para>
/// </summary>
public class BalanceThresholdSettlementPolicy(
    ISettlementConfigProvider configProvider,
    ILogger<BalanceThresholdSettlementPolicy>? logger = null) : ISettlementPolicy
{
    /// <inheritdoc />
    public async Task<SettlementPlan?> EvaluateAsync(
        SettlementContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.AvailableBalanceSats <= 0)
            return null;

        var configs = await configProvider.GetConfigs(context.WalletId, cancellationToken);

        // Providers are free to ignore the walletId filter, so filter again here.
        var candidates = configs
            .Where(config => config.Enabled && config.WalletId == context.WalletId)
            .Where(config => context.AvailableBalanceSats >= config.ThresholdSats)
            .OrderBy(config => config.ThresholdSats)
            .ToArray();

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

            return new SettlementPlan(config.Destination, amount);
        }

        return null;
    }
}
