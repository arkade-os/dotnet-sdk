namespace NArk.Abstractions.Wallets;

/// <summary>
/// The signing flavour of an <see cref="ArkWalletInfo"/>. Drives how
/// <see cref="IWalletProvider.GetSignerAsync"/> materializes an
/// <see cref="IArkadeWalletSigner"/> for the wallet — or whether it
/// returns <c>null</c> because the wallet has no local signer.
/// </summary>
public enum WalletType
{
    /// <summary>
    /// Legacy nsec-style wallet: a single Schnorr private key stored as the
    /// wallet's <see cref="ArkWalletInfo.Secret"/>. The signer is produced
    /// directly from that key. <see cref="ArkWalletInfo.Secret"/> MUST be
    /// non-null/non-empty.
    /// </summary>
    SingleKey = 0,

    /// <summary>
    /// BIP-39/BIP-32 hierarchical-deterministic wallet: the
    /// <see cref="ArkWalletInfo.Secret"/> is a mnemonic, and individual
    /// signing keys are derived from it on demand using the wallet's
    /// <see cref="ArkWalletInfo.AccountDescriptor"/>.
    /// <see cref="ArkWalletInfo.Secret"/> MUST be non-null/non-empty.
    /// </summary>
    HD = 1,

    /// <summary>
    /// Watch-only wallet: the SDK can derive addresses and observe VTXOs
    /// from the wallet's <see cref="ArkWalletInfo.AccountDescriptor"/>, but
    /// holds no signing material. <see cref="IWalletProvider.GetSignerAsync"/>
    /// returns <c>null</c> for this flavour, and signing-dependent operations
    /// (batch participation, unilateral exits, etc.) will throw with a
    /// descriptive error. <see cref="ArkWalletInfo.Secret"/> MUST be null
    /// or empty.
    /// </summary>
    WatchOnly = 2,

    /// <summary>
    /// Remote-signing wallet: the signer is proxied to an external
    /// <see cref="IRemoteSignerTransport"/> registered in DI. The SDK never
    /// sees the private material; every signing call (pubkey, MuSig nonce,
    /// MuSig partial signature, Schnorr signature) is forwarded to the
    /// transport. <see cref="ArkWalletInfo.Secret"/> MUST be null or empty.
    /// </summary>
    Remote = 3
}
