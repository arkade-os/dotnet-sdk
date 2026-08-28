namespace NArk.Abstractions.Settlement;

/// <summary>
/// Supplies the per-wallet settlement rules the built-in policies act on. The SDK
/// deliberately persists nothing here — the application already owns this
/// configuration (a store setting, a user preference, a config file) and implements
/// this interface over it.
/// </summary>
public interface ISettlementConfigProvider
{
    /// <summary>
    /// Returns the configured settlement rules, optionally narrowed to one wallet.
    /// Return an empty collection when nothing is configured.
    /// </summary>
    /// <param name="walletId">Wallet to filter by, or <see langword="null"/> for every configured wallet.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyCollection<SettlementConfig>> GetConfigs(
        string? walletId = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One settlement rule: when a wallet's available balance of <paramref name="SourceAsset"/>
/// reaches <paramref name="Threshold"/>, sweep it to <paramref name="Destination"/>.
/// </summary>
/// <param name="WalletId">The wallet this rule applies to.</param>
/// <param name="Destination">Where the settlement sends funds.</param>
/// <param name="Threshold">
/// Balance at which the rule fires, in <paramref name="SourceAsset"/>'s atomic units —
/// satoshis for BTC. The threshold gates <em>when</em> a settlement happens, not how much
/// moves — a firing settlement sweeps the whole available balance of that asset (capped by
/// <paramref name="MaxAmount"/>). Zero fires as soon as anything is spendable.
/// </param>
/// <param name="SourceAsset">
/// What leaves the wallet, and what the threshold measures: <see cref="SettlementAssets.Btc"/>
/// (the default) or the id of an Arkade-issued asset. BTC and assets are measured
/// independently — an asset rule never fires on the wallet's satoshi balance, and a BTC rule
/// never fires on the dust an asset carrier holds. Configure one rule per asset you settle.
/// </param>
/// <param name="Enabled">Set to <see langword="false"/> to keep the rule stored but inert.</param>
/// <param name="MaxAmount">
/// Upper bound on a single settlement, in <paramref name="SourceAsset"/>'s atomic units. Use
/// it to stay under a rail's limit (a swap maximum, for example). <see langword="null"/>
/// means unbounded.
/// </param>
public record SettlementConfig(
    string WalletId,
    SettlementDestination Destination,
    long Threshold,
    string SourceAsset = SettlementAssets.Btc,
    bool Enabled = true,
    long? MaxAmount = null);
