namespace NArk.Abstractions.Wallets;

/// <summary>
/// The key-derivation flavour of an <see cref="ArkWalletInfo"/>. This is strictly about
/// <i>how scripts are derived</i> — a single tweaked key vs. an xpub-derived child set —
/// and stays orthogonal to whether the wallet can sign locally, remote-sign, or only watch.
/// <para>Signing capability is answered by <see cref="IWalletProvider.GetSignerAsync"/>: it
/// returns a signer when one is available (local key in <see cref="ArkWalletInfo.Secret"/>,
/// or a remote signer registered for this wallet) and <c>null</c> for watch-only.</para>
/// </summary>
public enum WalletType
{
    /// <summary>
    /// Legacy nsec-style wallet: a single Schnorr key pair. The script is a flat
    /// <c>tr(pubkey)</c>; child derivation does not apply.
    /// </summary>
    SingleKey = 0,

    /// <summary>
    /// BIP-32/BIP-86 hierarchical-deterministic wallet: scripts are derived from the
    /// wallet's account xpub (<see cref="ArkWalletInfo.AccountDescriptor"/>) at a
    /// running child index. The mnemonic (when present in <see cref="ArkWalletInfo.Secret"/>)
    /// allows local signing; absence means watch-only or remote-signed at the signer-provider
    /// level — see <see cref="IWalletProvider.GetSignerAsync"/>.
    /// </summary>
    HD = 1,
}
