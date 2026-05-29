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
///   <item><description>The signer keeps the secret nonce indexed by <paramref name="context"/>'s aggregate pubkey.</description></item>
///   <item><description><see cref="SignMusig"/> looks the secret nonce back up by the same context, uses it, and consumes it.</description></item>
/// </list>
/// Callers pass the same <see cref="MusigContext"/> instance (or one with an identical
/// <see cref="MusigContext.AggregatePubKey"/>) to both calls. Calling <see cref="SignMusig"/>
/// without a prior matching <see cref="GenerateNonces"/> on the same signer throws.
/// </summary>
public interface IArkadeWalletSigner
{
    /// <summary>
    /// Gets the compressed public key for the given descriptor, preserving parity.
    /// </summary>
    Task<ECPubKey> GetPubKey(OutputDescriptor descriptor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Produces a MuSig2 partial signature for the given context using the descriptor's
    /// private key and the secret nonce generated for the same context by a prior call
    /// to <see cref="GenerateNonces"/>. The secret nonce is consumed and cannot be reused
    /// for another <see cref="SignMusig"/> call (MuSig2 nonce reuse leaks the private key).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No secret nonce is stored for <paramref name="context"/> — <see cref="GenerateNonces"/>
    /// was not called for this context on this signer instance, or the nonce was already consumed.
    /// </exception>
    Task<MusigPartialSignature> SignMusig(
        OutputDescriptor descriptor,
        MusigContext context,
        CancellationToken cancellationToken = default);


    Task<(ECXOnlyPubKey, SecpSchnorrSignature)> Sign(
        OutputDescriptor descriptor,
        uint256 hash,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a fresh MuSig2 nonce pair for <paramref name="context"/>, retains the
    /// secret half indexed by the context's aggregate pubkey, and returns the public half.
    /// Calling twice for the same context (without an intervening <see cref="SignMusig"/>
    /// to consume the prior nonce) throws — generating a fresh nonce on top of an unused
    /// one would orphan secret material in the signer's store.
    /// </summary>
    Task<MusigPubNonce> GenerateNonces(
        OutputDescriptor descriptor,
        MusigContext context,
        CancellationToken cancellationToken = default
    );
}