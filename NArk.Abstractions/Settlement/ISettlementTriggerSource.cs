namespace NArk.Abstractions.Settlement;

/// <summary>
/// Feeds wallet activity into the settlement engine from outside <c>NArk.Core</c>.
/// The engine already reacts to VTXO and intent changes; register a trigger source to
/// add another signal — a swap package uses one to re-evaluate a wallet whenever a
/// swap changes state.
/// </summary>
public interface ISettlementTriggerSource
{
    /// <summary>
    /// Raised with the wallet identifier that should be re-evaluated. Raising it for a
    /// wallet with no settlement configuration is harmless — the engine deduplicates
    /// queued wallets and skips unconfigured ones.
    /// </summary>
    event EventHandler<string>? WalletActivity;
}
