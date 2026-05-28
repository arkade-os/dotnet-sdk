namespace NArk.Abstractions.Wallets;

/// <summary>
/// Wallet information record used at the abstraction/interface boundary.
/// </summary>
/// <param name="Id">Wallet identifier.</param>
/// <param name="Secret">
/// Signing material for the wallet, interpreted according to
/// <paramref name="WalletType"/>:
/// <list type="bullet">
///   <item><description><see cref="Wallets.WalletType.SingleKey"/>: the nsec private key — MUST be non-null and non-empty.</description></item>
///   <item><description><see cref="Wallets.WalletType.HD"/>: the BIP-39 mnemonic — MUST be non-null and non-empty.</description></item>
///   <item><description><see cref="Wallets.WalletType.WatchOnly"/>: MUST be null or empty — the wallet holds no signing material.</description></item>
///   <item><description><see cref="Wallets.WalletType.Remote"/>: MUST be null or empty — signing is proxied via <see cref="IRemoteSignerTransport"/>.</description></item>
/// </list>
/// </param>
/// <param name="Destination">Destination address for swept funds.</param>
/// <param name="WalletType">Wallet flavour — see <see cref="Wallets.WalletType"/>.</param>
/// <param name="AccountDescriptor">For HD wallets: the account descriptor. For legacy: <c>tr(pubkey)</c>.</param>
/// <param name="LastUsedIndex">For HD wallets: last used derivation index.</param>
/// <param name="Metadata">
/// Generic per-wallet key-value store (persisted as a JSON column). Used for
/// per-wallet bookkeeping that doesn't warrant a dedicated column —
/// e.g. <c>"vtxo.lastFullPollAt"</c> for the VTXO sync cursor. Use sparse-key
/// updates via <c>IWalletStorage.SetMetadataValue</c> so concurrent writers
/// for different concerns don't clobber each other.
/// </param>
public record ArkWalletInfo(
    string Id,
    string? Secret,
    string? Destination,
    WalletType WalletType,
    string? AccountDescriptor,
    int LastUsedIndex,
    IReadOnlyDictionary<string, string>? Metadata = null);
