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
/// <b>SECURITY LIMITATION (phase 1):</b> <see cref="GenerateNoncesAsync"/> currently returns
/// the MuSig2 secret nonce to the SDK and <see cref="SignMusigAsync"/> accepts it back across
/// the transport. The cryptographic argument for remote signing — that private material never
/// leaves the signer — does <i>not</i> hold under this interface. The intended phase-2 shape
/// has <see cref="GenerateNoncesAsync"/> returning only the public nonce and
/// <see cref="SignMusigAsync"/> taking no nonce parameter (the signer remembers it indexed by
/// context). Tracked in <see href="https://github.com/arkade-os/dotnet-sdk/issues/111">#111</see>;
/// do not rely on nonce confidentiality of an <see cref="IRemoteSignerTransport"/>
/// implementation until that is fixed.
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
    /// Produces a MuSig2 partial signature for the given context and nonce
    /// using the descriptor's private key.
    /// </summary>
    /// <param name="walletId">The wallet whose key signs.</param>
    /// <param name="descriptor">The descriptor identifying the signing key.</param>
    /// <param name="context">The MuSig2 context (cosigner set + sighash).</param>
    /// <param name="nonce">The secret nonce produced earlier by <see cref="GenerateNoncesAsync"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    // TODO: SECURITY — drop the `nonce` parameter and have the signer dereference
    // the private nonce it kept after GenerateNoncesAsync via a context key. Phase-2,
    // tracked at https://github.com/arkade-os/dotnet-sdk/issues/111.
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
    // TODO: SECURITY — return MusigPubNonce instead and keep the private nonce on
    // the signer indexed by context. Today the SDK receives the secret half, which
    // breaks the cryptographic argument for remote signing. Phase-2, tracked at
    // https://github.com/arkade-os/dotnet-sdk/issues/111.
    Task<MusigPrivNonce> GenerateNoncesAsync(
        string walletId,
        OutputDescriptor descriptor,
        MusigContext context,
        CancellationToken cancellationToken = default);
}
