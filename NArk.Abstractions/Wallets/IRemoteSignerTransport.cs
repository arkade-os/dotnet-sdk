using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NBitcoin.Secp256k1.Musig;

namespace NArk.Abstractions.Wallets;

/// <summary>
/// Transport abstraction over a remote signer. Mirrors <see cref="IArkadeWalletSigner"/> but
/// adds a <c>walletId</c> argument to every call so a single transport instance can serve
/// multiple wallets (e.g. a multi-user server-side signing service, an HWI bridge, or a
/// browser-extension wallet shared across tabs).
/// </summary>
/// <remarks>
/// Register an implementation in DI alongside any wallet whose <see cref="ArkWalletInfo.Secret"/>
/// is null/empty (i.e. no local signing material). The default <see cref="IWalletProvider"/>
/// implementation probes <see cref="KnowsWalletAsync"/> to decide whether such a wallet is
/// remote-signed (signer is a <see cref="IArkadeWalletSigner"/> proxy over this transport) or
/// watch-only (<see cref="IWalletProvider.GetSignerAsync"/> returns <c>null</c>). Capability is
/// answered by this interface, not by a flag on the wallet record.
/// <para>
/// The MuSig2 nonce flow keeps the secret half on the transport side:
/// <see cref="GenerateNoncesAsync"/> returns only the public nonce, the implementation retains
/// the secret half indexed by the context, and <see cref="SignMusigAsync"/> consumes it on use.
/// Implementations need an eviction policy for abandoned nonces (TTL or bounded count) so the
/// store does not grow without bound — the SDK-side <see cref="IArkadeWalletSigner"/> contract
/// requires SignMusig to throw if no matching nonce is found.
/// </para>
/// </remarks>
public interface IRemoteSignerTransport
{
    /// <summary>
    /// Indicates whether this transport can sign for the given wallet. Used by the wallet
    /// provider to distinguish remote-signed wallets from watch-only ones when the wallet has
    /// no local <see cref="ArkWalletInfo.Secret"/>: <c>true</c> → produce a remote-signer proxy;
    /// <c>false</c> → fall through to watch-only (signer = null).
    /// </summary>
    Task<bool> KnowsWalletAsync(string walletId, CancellationToken cancellationToken = default);


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
    /// Produces a MuSig2 partial signature for the given context using the descriptor's
    /// private key and the secret nonce the transport retained when <see cref="GenerateNoncesAsync"/>
    /// was called for the same context. The secret nonce is consumed on this call.
    /// </summary>
    /// <param name="walletId">The wallet whose key signs.</param>
    /// <param name="descriptor">The descriptor identifying the signing key.</param>
    /// <param name="context">The MuSig2 context (cosigner set + sighash) the nonce was generated for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    /// No secret nonce is stored for <paramref name="walletId"/> + <paramref name="context"/>
    /// (it was never generated, was already consumed, or has been evicted by the transport's
    /// eviction policy).
    /// </exception>
    Task<MusigPartialSignature> SignMusigAsync(
        string walletId,
        OutputDescriptor descriptor,
        MusigContext context,
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
    /// Generates a fresh MuSig2 nonce pair for the supplied context, retains the secret
    /// half on the transport side indexed by <paramref name="walletId"/> +
    /// <paramref name="context"/>, and returns the public half so the caller can complete
    /// nonce aggregation with cosigners. The secret half never crosses the transport
    /// boundary — that is the cryptographic claim of remote signing.
    /// </summary>
    /// <param name="walletId">The wallet whose key contributes the nonce.</param>
    /// <param name="descriptor">The descriptor identifying the signing key.</param>
    /// <param name="context">The MuSig2 context the nonce is generated for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MusigPubNonce> GenerateNoncesAsync(
        string walletId,
        OutputDescriptor descriptor,
        MusigContext context,
        CancellationToken cancellationToken = default);
}
