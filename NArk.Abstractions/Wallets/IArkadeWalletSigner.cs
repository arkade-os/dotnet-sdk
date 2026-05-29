using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NBitcoin.Secp256k1.Musig;

namespace NArk.Abstractions.Wallets;

/// <summary>
/// Wallet-side signer used during MuSig2 batch participation. The MuSig2 nonce flow is
/// designed so the secret half never leaves the signer:
/// <list type="number">
///   <item><description><see cref="GenerateNonces"/> derives the secret nonce internally and returns only the public half.</description></item>
///   <item><description>The signer keeps the secret nonce indexed by the caller-supplied <c>sessionId</c>.</description></item>
///   <item><description><see cref="SignMusig"/> looks the secret nonce back up by the same <c>sessionId</c>, uses it, and consumes it.</description></item>
/// </list>
/// The caller must pass a <em>session-unique</em> <c>sessionId</c> to both calls — typically a
/// transaction identifier (e.g. a tree-node txid in batch participation), or any string the
/// caller can correlate. <see cref="MusigContext.AggregatePubKey"/> on its own is <em>not</em>
/// unique per signing operation: in a batch tree, multiple transactions can share the same
/// cosigner set and taproot tweak, so their contexts have identical aggregate pubkeys but
/// different sighashes. The sighash is buried inside <see cref="MusigContext"/> and cannot
/// be observed by the signer, so disambiguation has to be caller-supplied.
/// </summary>
public interface IArkadeWalletSigner
{
    /// <summary>
    /// Gets the compressed public key for the given descriptor, preserving parity.
    /// </summary>
    Task<ECPubKey> GetPubKey(OutputDescriptor descriptor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Produces a MuSig2 partial signature for the given context using the descriptor's
    /// private key and the secret nonce generated under the same <paramref name="sessionId"/>
    /// by a prior call to <see cref="GenerateNonces"/>. The secret nonce is consumed and cannot
    /// be reused for another <see cref="SignMusig"/> call (MuSig2 nonce reuse leaks the private
    /// key).
    /// </summary>
    /// <param name="descriptor">The descriptor identifying the signing key.</param>
    /// <param name="context">The MuSig2 context (cosigner set + sighash) the nonce was generated for.</param>
    /// <param name="sessionId">
    /// The same session identifier that was passed to the matching <see cref="GenerateNonces"/>
    /// call. Typically a transaction identifier (txid) or any other string unique to this signing
    /// operation within the signer's scope.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    /// No secret nonce is stored for <paramref name="sessionId"/> — <see cref="GenerateNonces"/>
    /// was not called for this session on this signer, or the nonce was already consumed.
    /// </exception>
    Task<MusigPartialSignature> SignMusig(
        OutputDescriptor descriptor,
        MusigContext context,
        string sessionId,
        CancellationToken cancellationToken = default);


    Task<(ECXOnlyPubKey, SecpSchnorrSignature)> Sign(
        OutputDescriptor descriptor,
        uint256 hash,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a fresh MuSig2 nonce pair for <paramref name="context"/>, retains the
    /// secret half indexed by <paramref name="sessionId"/>, and returns the public half.
    /// Calling twice with the same <paramref name="sessionId"/> (without an intervening
    /// <see cref="SignMusig"/> to consume the prior nonce) throws — generating a fresh nonce
    /// on top of an unused one would orphan secret material in the signer's store.
    /// </summary>
    /// <param name="descriptor">The descriptor identifying the signing key.</param>
    /// <param name="context">The MuSig2 context the nonce is generated for.</param>
    /// <param name="sessionId">
    /// A caller-supplied identifier unique to this signing operation. Must match the value
    /// later passed to <see cref="SignMusig"/>. Typically a transaction identifier (txid).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MusigPubNonce> GenerateNonces(
        OutputDescriptor descriptor,
        MusigContext context,
        string sessionId,
        CancellationToken cancellationToken = default
    );
}