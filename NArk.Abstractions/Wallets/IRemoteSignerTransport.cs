using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NBitcoin.Secp256k1.Musig;

namespace NArk.Abstractions.Wallets;

/// <summary>
/// Transport abstraction over a remote signer. Mirrors
/// <see cref="IArkadeWalletSigner"/> but adds a <c>walletId</c> argument to
/// every call so a single transport instance can serve multiple wallets
/// (e.g. a multi-user server-side signing service, an HWI bridge, or a
/// browser-extension wallet shared across tabs).
/// </summary>
/// <remarks>
/// Register an implementation in DI alongside a wallet of type
/// <see cref="WalletType.Remote"/>. The default
/// <see cref="IWalletProvider"/> implementation will wrap the transport in
/// an <c>IArkadeWalletSigner</c> proxy when <see cref="IWalletProvider.GetSignerAsync"/>
/// is called for a Remote wallet.
/// </remarks>
public interface IRemoteSignerTransport
{
    /// <summary>
    /// Gets the compressed public key for the given descriptor, preserving parity.
    /// </summary>
    /// <param name="walletId">The wallet whose key is being requested.</param>
    /// <param name="descriptor">The descriptor identifying the key to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ECPubKey> GetPubKeyAsync(
        string walletId,
        OutputDescriptor descriptor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Produces a MuSig2 partial signature for the given context and nonce
    /// using the descriptor's private key.
    /// </summary>
    /// <param name="walletId">The wallet whose key signs.</param>
    /// <param name="descriptor">The descriptor identifying the signing key.</param>
    /// <param name="context">The MuSig2 context (cosigner set + sighash).</param>
    /// <param name="nonce">The secret nonce produced earlier by <see cref="GenerateNoncesAsync"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MusigPartialSignature> SignMusigAsync(
        string walletId,
        OutputDescriptor descriptor,
        MusigContext context,
        MusigPrivNonce nonce,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Produces a BIP-340 Schnorr signature over <paramref name="hash"/> using
    /// the descriptor's private key, returning the x-only pubkey alongside the
    /// signature.
    /// </summary>
    /// <param name="walletId">The wallet whose key signs.</param>
    /// <param name="descriptor">The descriptor identifying the signing key.</param>
    /// <param name="hash">The 32-byte hash to sign.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<(ECXOnlyPubKey, SecpSchnorrSignature)> SignAsync(
        string walletId,
        OutputDescriptor descriptor,
        uint256 hash,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates the secret nonce for the supplied MuSig2 context. The returned
    /// nonce is bound to <paramref name="context"/> and must be passed back
    /// into <see cref="SignMusigAsync"/> on the same context.
    /// </summary>
    /// <param name="walletId">The wallet whose key contributes the nonce.</param>
    /// <param name="descriptor">The descriptor identifying the signing key.</param>
    /// <param name="context">The MuSig2 context the nonce is generated for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MusigPrivNonce> GenerateNoncesAsync(
        string walletId,
        OutputDescriptor descriptor,
        MusigContext context,
        CancellationToken cancellationToken = default);
}
