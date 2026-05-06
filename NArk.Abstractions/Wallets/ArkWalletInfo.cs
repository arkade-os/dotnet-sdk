namespace NArk.Abstractions.Wallets;

/// <summary>
/// Wallet information record used at the abstraction/interface boundary.
/// </summary>
/// <param name="Id">Wallet identifier.</param>
/// <param name="Secret">For nsec wallets: the nsec private key. For HD wallets: the BIP-39 mnemonic.</param>
/// <param name="Destination">Destination address for swept funds.</param>
/// <param name="WalletType">Wallet flavour (legacy nsec or HD mnemonic).</param>
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
    string Secret,
    string? Destination,
    WalletType WalletType,
    string? AccountDescriptor,
    int LastUsedIndex,
    IReadOnlyDictionary<string, string>? Metadata = null);
