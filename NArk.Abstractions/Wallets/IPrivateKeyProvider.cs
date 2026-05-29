using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NBitcoin.Secp256k1.Musig;

namespace NArk.Abstractions.Wallets;

/// <summary>
/// A descriptor-scoped source of signing operations. The provider owns the private key (locally
/// or by reference) and exposes the signer operations rooted in it — it never returns the raw
/// key, which is what lets a remote-signing implementation honour the same contract.
/// <para>
/// A wallet's signer is a composition of one or more providers (see
/// <see cref="IArkadeWalletSigner"/> implementations that compose providers). Each provider
/// answers <see cref="CanProvideAsync"/> for the descriptors it owns; the composing signer
/// dispatches each call to the first provider that claims the descriptor.
/// </para>
/// <para>
/// The MuSig2 nonce lifecycle from <see cref="IArkadeWalletSigner"/> applies per-provider:
/// <see cref="GenerateNoncesAsync"/> retains the secret half indexed by <c>sessionId</c>, and
/// <see cref="SignMusigAsync"/> consumes it on use. Different providers maintain independent
/// nonce stores — there is no cross-provider sharing.
/// </para>
/// </summary>
public interface IPrivateKeyProvider
{
    /// <summary>
    /// Returns <c>true</c> iff this provider owns signing material for <paramref name="descriptor"/>.
    /// Implementations should make this cheap (fingerprint / x-only match) — it is called on
    /// every dispatch and may run against the BIP-32 / BIP-39 derivation path of long descriptors.
    /// </summary>
    Task<bool> CanProvideAsync(OutputDescriptor descriptor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the compressed public key for <paramref name="descriptor"/>, preserving parity.
    /// </summary>
    Task<ECPubKey> GetPubKeyAsync(OutputDescriptor descriptor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Produces a BIP-340 Schnorr signature over <paramref name="hash"/> using the descriptor's
    /// private key, returning the x-only pubkey alongside the signature.
    /// </summary>
    Task<(ECXOnlyPubKey, SecpSchnorrSignature)> SignAsync(
        OutputDescriptor descriptor,
        uint256 hash,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a fresh MuSig2 nonce pair for <paramref name="context"/>, retains the secret
    /// half indexed by <paramref name="sessionId"/>, and returns the public half. See
    /// <see cref="IArkadeWalletSigner.GenerateNonces"/> for the lifecycle contract.
    /// </summary>
    Task<MusigPubNonce> GenerateNoncesAsync(
        OutputDescriptor descriptor,
        MusigContext context,
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Produces a MuSig2 partial signature using the secret nonce generated for the same
    /// <paramref name="sessionId"/>. The secret nonce is consumed on this call. See
    /// <see cref="IArkadeWalletSigner.SignMusig"/> for the lifecycle contract.
    /// </summary>
    Task<MusigPartialSignature> SignMusigAsync(
        OutputDescriptor descriptor,
        MusigContext context,
        string sessionId,
        CancellationToken cancellationToken = default);
}
