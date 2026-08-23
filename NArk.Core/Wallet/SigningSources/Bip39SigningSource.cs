using System.Collections.Concurrent;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Wallets;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NBitcoin.Secp256k1.Musig;

namespace NArk.Core.Wallet.SigningSources;

/// <summary>
/// A BIP-39 mnemonic signing source. Claims any descriptor whose origin fingerprint matches
/// the mnemonic's master fingerprint, and derives the per-descriptor private key on demand.
/// </summary>
/// <remarks>
/// BIP-39 → BIP-32 ExtKey derivation is PBKDF2-HMAC-SHA512 × 2048 iterations (~100 ms on
/// commodity CPUs); we cache the ExtKey globally so the cost is amortised across instances.
/// </remarks>
public class Bip39SigningSource : IDescriptorSigningSource
{
    // Mnemonic → ExtKey is a pure function of the mnemonic string; cache it globally so PBKDF2
    // runs at most once per mnemonic per process. The mnemonic is already held in memory by
    // the wallet object, so this introduces no new exposure surface.
    private static readonly ConcurrentDictionary<string, ExtKey> _extKeyCache = new();

    // Per-session secret nonce store, keyed by caller-supplied sessionId (see IArkadeWalletSigner).
    private readonly ConcurrentDictionary<string, MusigPrivNonce> _secNonces = new();

    private readonly string _mnemonic;
    private readonly HDFingerprint _masterFingerprint;

    public Bip39SigningSource(string mnemonic)
    {
        if (string.IsNullOrWhiteSpace(mnemonic))
            throw new ArgumentException("Mnemonic is required.", nameof(mnemonic));
        _mnemonic = mnemonic;
        _masterFingerprint = GetExtKey().GetPublicKey().GetHDFingerPrint();
    }

    public Task<bool> CanProvideAsync(OutputDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        var metadata = descriptor.Extract();
        // Only HD-origin descriptors are derivable from a mnemonic. Nsec-style bare-tr
        // descriptors have no origin and a fingerprint mismatch by definition.
        var match = metadata.AccountPath is { } accountPath
                    && accountPath.MasterFingerprint == _masterFingerprint;
        return Task.FromResult(match);
    }

    public Task<ECPubKey> GetPubKeyAsync(OutputDescriptor descriptor, CancellationToken cancellationToken = default)
        => Task.FromResult(DerivePrivateKey(descriptor).CreatePubKey());

    public Task<(ECXOnlyPubKey, SecpSchnorrSignature)> SignAsync(OutputDescriptor descriptor, uint256 hash, CancellationToken cancellationToken = default)
    {
        var privKey = DerivePrivateKey(descriptor);
        return Task.FromResult((privKey.CreateXOnlyPubKey(), privKey.SignBIP340(hash.ToBytes(), new byte[32])));
    }

    public Task<MusigPubNonce> GenerateNoncesAsync(OutputDescriptor descriptor, MusigContext context,
        string sessionId, CancellationToken cancellationToken = default)
    {
        if (_secNonces.ContainsKey(sessionId))
            throw new InvalidOperationException(
                $"A secret nonce is already stored for sessionId '{sessionId}'. " +
                "Call SignMusig to consume it before generating a fresh nonce for the same session; " +
                "MuSig2 nonce reuse leaks the private key.");
        var privKey = DerivePrivateKey(descriptor);
        var nonce = context.GenerateNonce(privKey);
        if (!_secNonces.TryAdd(sessionId, nonce))
            throw new InvalidOperationException(
                $"A secret nonce was concurrently stored for sessionId '{sessionId}'. " +
                "sessionId must be unique per signing operation.");
        return Task.FromResult(nonce.CreatePubNonce());
    }

    public Task<MusigPartialSignature> SignMusigAsync(OutputDescriptor descriptor, MusigContext context,
        string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_secNonces.TryRemove(sessionId, out var nonce))
            throw new InvalidOperationException(
                $"No secret nonce stored for sessionId '{sessionId}'. " +
                "Call GenerateNonces with the same sessionId first; nonces are consumed on use and cannot be replayed.");
        var privKey = DerivePrivateKey(descriptor);
        return Task.FromResult(context.Sign(privKey, nonce));
    }

    private ECPrivKey DerivePrivateKey(OutputDescriptor descriptor)
    {
        var fullPath = descriptor.Extract().FullPath
                       ?? throw new InvalidOperationException(
                           "Descriptor has no full derivation path; cannot derive a BIP-39 key for it.");
        return ECPrivKey.Create(GetExtKey().Derive(fullPath).PrivateKey.ToBytes());
    }

    private ExtKey GetExtKey() =>
        _extKeyCache.GetOrAdd(_mnemonic, secret => new Mnemonic(secret).DeriveExtKey());
}
