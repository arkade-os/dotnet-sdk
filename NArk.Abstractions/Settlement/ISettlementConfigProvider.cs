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
/// One settlement rule: when a wallet's available balance reaches
/// <paramref name="ThresholdSats"/>, sweep it to <paramref name="Destination"/>.
/// </summary>
/// <param name="WalletId">The wallet this rule applies to.</param>
/// <param name="Destination">Where the settlement sends funds.</param>
/// <param name="ThresholdSats">
/// Balance at which the rule fires, in satoshis. The threshold gates <em>when</em> a
/// settlement happens, not how much moves — a firing settlement sweeps the whole
/// available balance (capped by <paramref name="MaxAmountSats"/>). Zero fires as soon
/// as anything is spendable.
/// </param>
/// <param name="Enabled">Set to <see langword="false"/> to keep the rule stored but inert.</param>
/// <param name="MaxAmountSats">
/// Upper bound on a single settlement, in satoshis. Use it to stay under a rail's limit
/// (a swap maximum, for example). <see langword="null"/> means unbounded.
/// </param>
public record SettlementConfig(
    string WalletId,
    SettlementDestination Destination,
    long ThresholdSats,
    bool Enabled = true,
    long? MaxAmountSats = null);
