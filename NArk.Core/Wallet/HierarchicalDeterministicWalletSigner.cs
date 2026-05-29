using NArk.Abstractions.Extensions;
using NArk.Abstractions.Wallets;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NBitcoin.Secp256k1.Musig;

namespace NArk.Core.Wallet;

public class HierarchicalDeterministicWalletSigner(ArkWalletInfo wallet) : IArkadeWalletSigner
{
    // BIP-39 → BIP-32 master extKey requires PBKDF2-HMAC-SHA512 × 2048 iterations,
    // ~100 ms per call on commodity CPUs. The plugin recreates this signer for
    // every GetPubKey / Sign / SignMusig / GenerateNonces — so on a busy
    // batch-session path the wallet does *hundreds* of PBKDF2 rounds per minute,
    // pegging a CPU core and starving every other async task in the process
    // (we observed Stopwatch-wall-time on unrelated work blowing up to 1-3 s
    // during these windows). Mnemonic → ExtKey is a pure function of the
    // mnemonic string; cache it globally so PBKDF2 runs at most once per
    // mnemonic per process. The mnemonic is already held in memory by the
    // wallet object, so this introduces no new exposure surface.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ExtKey> _extKeyCache = new();

    // Per-instance secret nonce store; see NSecWalletSigner for the rationale.
    // Keyed by the (tweaked) aggregate pubkey hex of the MuSig2 context.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, MusigPrivNonce> _secNonces = new();

    private Task<ECPrivKey> DerivePrivateKey(OutputDescriptor descriptor)
    {
        var fullPath = descriptor.Extract().FullPath ?? throw new InvalidOperationException();
        var mnemonic = wallet.Secret ?? throw new InvalidOperationException(
            $"HD wallet '{wallet.Id}' has no Secret — a BIP-39 mnemonic is required for HD wallets.");
        var extKey = _extKeyCache.GetOrAdd(mnemonic, secret => new Mnemonic(secret).DeriveExtKey());
        return Task.FromResult(ECPrivKey.Create(extKey.Derive(fullPath).PrivateKey.ToBytes()));
    }

    public async Task<ECPubKey> GetPubKey(OutputDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        var privKey = await DerivePrivateKey(descriptor);
        return privKey.CreatePubKey();
    }

    public async Task<MusigPartialSignature> SignMusig(OutputDescriptor descriptor, MusigContext context,
        CancellationToken cancellationToken = default)
    {
        var key = ContextKey(context);
        if (!_secNonces.TryRemove(key, out var nonce))
            throw new InvalidOperationException(
                $"No secret nonce stored for the given MuSig2 context (aggregate pubkey {key}). " +
                "Call GenerateNonces for this context first; nonces are consumed on use and cannot be replayed.");
        var privKey = await DerivePrivateKey(descriptor);
        return context.Sign(privKey, nonce);
    }

    public async Task<(ECXOnlyPubKey, SecpSchnorrSignature)> Sign(OutputDescriptor descriptor, uint256 hash, CancellationToken cancellationToken = default)
    {
        var privKey = await DerivePrivateKey(descriptor);
        return (privKey.CreateXOnlyPubKey(), privKey.SignBIP340(hash.ToBytes()));
    }

    public async Task<MusigPubNonce> GenerateNonces(OutputDescriptor descriptor, MusigContext context, CancellationToken cancellationToken = default)
    {
        var privKey = await DerivePrivateKey(descriptor);
        var nonce = context.GenerateNonce(privKey);
        var key = ContextKey(context);
        if (!_secNonces.TryAdd(key, nonce))
            throw new InvalidOperationException(
                $"A secret nonce is already stored for this MuSig2 context (aggregate pubkey {key}). " +
                "Call SignMusig to consume it before generating a fresh nonce for the same context; " +
                "MuSig2 nonce reuse leaks the private key.");
        return nonce.CreatePubNonce();
    }

    private static string ContextKey(MusigContext context) =>
        Convert.ToHexString(context.AggregatePubKey.ToBytes()).ToLowerInvariant();
}
