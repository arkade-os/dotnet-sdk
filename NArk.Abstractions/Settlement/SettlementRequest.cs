namespace NArk.Abstractions.Settlement;

/// <summary>
/// A single settlement execution: move <paramref name="AmountSats"/> out of
/// <paramref name="WalletId"/> to <paramref name="Destination"/>.
/// </summary>
/// <param name="WalletId">The wallet the funds leave.</param>
/// <param name="AmountSats">Amount to settle, in satoshis. Must be positive.</param>
/// <param name="Destination">Where the funds go.</param>
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
    long AmountSats,
    SettlementDestination Destination,
    IReadOnlyCollection<ArkCoin>? Coins = null,
    string? Reference = null);
