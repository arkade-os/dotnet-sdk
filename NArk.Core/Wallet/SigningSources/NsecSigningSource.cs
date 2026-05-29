using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Wallets;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NBitcoin.Secp256k1.Musig;

namespace NArk.Core.Wallet.SigningSources;

/// <summary>
/// Single-key signing source backed by an <see cref="ECPrivKey"/> (typically loaded from a
/// Nostr nsec). Claims any descriptor whose x-only pubkey matches the source's key — note that
/// <c>tr()</c> descriptors strip the parity prefix on serialization, so the source returns its
/// own correct-parity compressed pubkey from <see cref="GetPubKeyAsync"/> rather than trusting
/// <c>descriptor.ToPubKey()</c>.
/// </summary>
public class NsecSigningSource : IDescriptorSigningSource
{
    private readonly ECPrivKey _privateKey;
    private readonly ECPubKey _publicKey;
    private readonly ECXOnlyPubKey _xOnlyPubKey;
    private readonly ILogger? _logger;

    // Per-session secret nonce store, keyed by caller-supplied sessionId (see IArkadeWalletSigner).
    private readonly ConcurrentDictionary<string, MusigPrivNonce> _secNonces = new();

    public NsecSigningSource(ECPrivKey privateKey, ILogger? logger = null)
    {
        _privateKey = privateKey ?? throw new ArgumentNullException(nameof(privateKey));
        _publicKey = privateKey.CreatePubKey();
        _xOnlyPubKey = privateKey.CreateXOnlyPubKey();
        _logger = logger;
    }

    /// <summary>
    /// Builds an <see cref="NsecSigningSource"/> from a bech32-encoded Nostr nsec.
    /// </summary>
    public static NsecSigningSource FromNsec(string nsec, ILogger? logger = null)
    {
        var encoder = Bech32Encoder.ExtractEncoderFromString(nsec);
        encoder.StrictLength = false;
        encoder.SquashBytes = true;
        var keyData = encoder.DecodeDataRaw(nsec, out _);
        var privKey = ECPrivKey.Create(keyData);
        var source = new NsecSigningSource(privKey, logger);
        logger?.LogDebug("NsecSigningSource created: xonly={XOnlyPubKey}, compressed={CompressedPubKey}",
            Convert.ToHexString(source._xOnlyPubKey.ToBytes()).ToLowerInvariant(),
            Convert.ToHexString(source._publicKey.ToBytes()).ToLowerInvariant());
        return source;
    }

    public Task<bool> CanProvideAsync(OutputDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        var descriptorXOnly = descriptor.Extract().XOnlyPubKey;
        return Task.FromResult(descriptorXOnly.ToBytes().SequenceEqual(_xOnlyPubKey.ToBytes()));
    }

    public Task<ECPubKey> GetPubKeyAsync(OutputDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        EnsureMatches(descriptor);
        // Return the actual signer pubkey (with correct parity) rather than descriptor.ToPubKey()
        // which loses parity through tr() serialization roundtrip.
        return Task.FromResult(_publicKey);
    }

    public Task<(ECXOnlyPubKey, SecpSchnorrSignature)> SignAsync(OutputDescriptor descriptor, uint256 hash, CancellationToken cancellationToken = default)
    {
        EnsureMatches(descriptor);
        if (!_privateKey.TrySignBIP340(hash.ToBytes(), null, out var sig))
            throw new InvalidOperationException("Failed to sign data");
        return Task.FromResult((_xOnlyPubKey, sig));
    }

    public Task<MusigPubNonce> GenerateNoncesAsync(OutputDescriptor descriptor, MusigContext context,
        string sessionId, CancellationToken cancellationToken = default)
    {
        EnsureMatches(descriptor);
        if (_secNonces.ContainsKey(sessionId))
            throw new InvalidOperationException(
                $"A secret nonce is already stored for sessionId '{sessionId}'. " +
                "Call SignMusig to consume it before generating a fresh nonce for the same session; " +
                "MuSig2 nonce reuse leaks the private key.");
        _logger?.LogInformation(
            "GenerateNonces called. Descriptor={Descriptor}, SignerCompressed={SignerCompressed}, SessionId={SessionId}",
            descriptor.ToString(),
            Convert.ToHexString(_publicKey.ToBytes()).ToLowerInvariant(),
            sessionId);
        var nonce = context.GenerateNonce(_privateKey);
        if (!_secNonces.TryAdd(sessionId, nonce))
            throw new InvalidOperationException(
                $"A secret nonce was concurrently stored for sessionId '{sessionId}'. " +
                "sessionId must be unique per signing operation.");
        return Task.FromResult(nonce.CreatePubNonce());
    }

    public Task<MusigPartialSignature> SignMusigAsync(OutputDescriptor descriptor, MusigContext context,
        string sessionId, CancellationToken cancellationToken = default)
    {
        EnsureMatches(descriptor);
        if (!_secNonces.TryRemove(sessionId, out var nonce))
            throw new InvalidOperationException(
                $"No secret nonce stored for sessionId '{sessionId}'. " +
                "Call GenerateNonces with the same sessionId first; nonces are consumed on use and cannot be replayed.");
        _logger?.LogInformation(
            "SignMusig called. Descriptor={Descriptor}, SignerCompressed={SignerCompressed}, SessionId={SessionId}",
            descriptor.ToString(),
            Convert.ToHexString(_publicKey.ToBytes()).ToLowerInvariant(),
            sessionId);
        return Task.FromResult(context.Sign(_privateKey, nonce));
    }

    private void EnsureMatches(OutputDescriptor descriptor)
    {
        var descriptorXOnly = descriptor.Extract().XOnlyPubKey;
        if (!descriptorXOnly.ToBytes().SequenceEqual(_xOnlyPubKey.ToBytes()))
            throw new InvalidOperationException(
                $"Descriptor does not belong to this signing source. " +
                $"DescriptorXOnly={Convert.ToHexString(descriptorXOnly.ToBytes()).ToLowerInvariant()}, " +
                $"SourceXOnly={Convert.ToHexString(_xOnlyPubKey.ToBytes()).ToLowerInvariant()}");
    }
}
