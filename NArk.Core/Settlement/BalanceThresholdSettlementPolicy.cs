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
/// single settlement with <see cref="SettlementConfig.MaxAmount"/> when a rail has
/// an upper limit; with a cap in place a second matching rule can settle the remainder,
/// since the engine executes plans against a shrinking balance.
/// </para>
/// <para>
/// Each rule measures one denomination, named by <see cref="SettlementConfig.SourceAsset"/>:
/// satoshis for BTC, atomic units for an Arkade-issued asset. A rule on an asset reads only
/// that asset's balance, so a wallet holding an asset and no BTC still settles the asset, and
/// the dust its carriers hold never pushes a BTC rule over its threshold.
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
        if (context.AvailableBalanceSats <= 0 && context.AssetBalances.Count == 0)
            yield break;

        var configs = await configProvider.GetConfigs(context.WalletId, cancellationToken);

        // Providers are free to ignore the walletId filter, so filter again here. Wallet ids are
        // output descriptors, where case is part of the identity — unlike asset ids, which are
        // compared case-insensitively throughout.
        // Thresholds are only comparable within one denomination, so rules are grouped by
        // their source asset before being ordered lowest-first.
        var candidates = configs
            .Where(config => config.Enabled
                             && string.Equals(config.WalletId, context.WalletId, StringComparison.Ordinal))
            .Where(config => context.GetAvailableBalance(config.SourceAsset) >= config.Threshold)
            .OrderBy(config => config.SourceAsset, StringComparer.OrdinalIgnoreCase)
            .ThenBy(config => config.Threshold);

        foreach (var config in candidates)
        {
            var balance = context.GetAvailableBalance(config.SourceAsset);

            var amount = config.MaxAmount is { } max
                ? Math.Min(balance, max)
                : balance;

            if (amount <= 0)
            {
                logger?.LogWarning(
                    "Wallet {WalletId} matched a settlement rule with a non-positive cap {MaxAmount}; skipping",
                    context.WalletId, config.MaxAmount);
                continue;
            }

            logger?.LogDebug(
                "Wallet {WalletId} balance {Balance} {SourceAsset} reached threshold {Threshold}; planning settlement of {Amount} to {Network}/{Asset}",
                context.WalletId, balance, config.SourceAsset, config.Threshold, amount,
                config.Destination.Network, config.Destination.Asset);

            yield return new SettlementPlan(config.Destination, amount, config.SourceAsset);
        }
    }
}
