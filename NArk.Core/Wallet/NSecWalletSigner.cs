using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Wallets;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NBitcoin.Secp256k1.Musig;

namespace NArk.Core.Wallet;

public class NSecWalletSigner(ECPrivKey privateKey, ILogger? logger = null) : IArkadeWalletSigner
{
    private readonly ECPubKey _publicKey = privateKey.CreatePubKey();
    private readonly ECXOnlyPubKey _xOnlyPubKey = privateKey.CreateXOnlyPubKey();

    // Per-session secret nonce store. Keyed by the caller-supplied sessionId — typically a
    // txid in batch participation. AggregatePubKey alone is not unique per signing operation
    // in a batch tree (multiple tree nodes can share cosigner set + tweak), and the sighash
    // is internal to MusigContext and can't be observed by the signer; the caller is the only
    // one with enough information to produce a disambiguating session identifier.
    // ConcurrentDictionary lets GenerateNonces / SignMusig race safely if a caller parallelises
    // across sessions; each entry is removed on SignMusig consumption so the store doesn't grow.
    private readonly ConcurrentDictionary<string, MusigPrivNonce> _secNonces = new();

    public static NSecWalletSigner FromNsec(string nsec, ILogger? logger = null)
    {
        var encoder2 = Bech32Encoder.ExtractEncoderFromString(nsec);
        encoder2.StrictLength = false;
        encoder2.SquashBytes = true;
        var keyData2 = encoder2.DecodeDataRaw(nsec, out _);
        var privKey = ECPrivKey.Create(keyData2);
        var signer = new NSecWalletSigner(privKey, logger);
        logger?.LogDebug("NSecWalletSigner created: xonly={XOnlyPubKey}, compressed={CompressedPubKey}",
            Convert.ToHexString(signer._xOnlyPubKey.ToBytes()).ToLowerInvariant(),
            Convert.ToHexString(signer._publicKey.ToBytes()).ToLowerInvariant());
        return signer;
    }

    public Task<ECPubKey> GetPubKey(OutputDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        var descriptorXOnly = descriptor.Extract().XOnlyPubKey;
        var descriptorPubKey = descriptor.ToPubKey();

        logger?.LogInformation(
            "GetPubKey called. Descriptor={Descriptor}, DescriptorPubKey={DescriptorPubKey}, " +
            "DescriptorXOnly={DescriptorXOnly}, SignerPubKey={SignerPubKey}, SignerXOnly={SignerXOnly}",
            descriptor.ToString(),
            Convert.ToHexString(descriptorPubKey.ToBytes()).ToLowerInvariant(),
            Convert.ToHexString(descriptorXOnly.ToBytes()).ToLowerInvariant(),
            Convert.ToHexString(_publicKey.ToBytes()).ToLowerInvariant(),
            Convert.ToHexString(_xOnlyPubKey.ToBytes()).ToLowerInvariant());

        if (!descriptorXOnly.ToBytes().SequenceEqual(_xOnlyPubKey.ToBytes()))
            throw new InvalidOperationException(
                $"Descriptor does not belong to this wallet. " +
                $"DescriptorXOnly={Convert.ToHexString(descriptorXOnly.ToBytes()).ToLowerInvariant()}, " +
                $"SignerXOnly={Convert.ToHexString(_xOnlyPubKey.ToBytes()).ToLowerInvariant()}");

        logger?.LogInformation(
            "GetPubKey returning actual signer pubkey={SignerPubKey} (descriptor would have given {DescriptorPubKey})",
            Convert.ToHexString(_publicKey.ToBytes()).ToLowerInvariant(),
            Convert.ToHexString(descriptorPubKey.ToBytes()).ToLowerInvariant());

        return Task.FromResult(_publicKey);
    }

    public Task<MusigPartialSignature> SignMusig(OutputDescriptor descriptor, MusigContext context,
        string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_secNonces.TryRemove(sessionId, out var nonce))
            throw new InvalidOperationException(
                $"No secret nonce stored for sessionId '{sessionId}'. " +
                "Call GenerateNonces with the same sessionId first; nonces are consumed on use and cannot be replayed.");

        logger?.LogInformation(
            "SignMusig called. Descriptor={Descriptor}, SignerCompressed={SignerCompressed}, SessionId={SessionId}",
            descriptor.ToString(),
            Convert.ToHexString(_publicKey.ToBytes()).ToLowerInvariant(),
            sessionId);
        var sig = context.Sign(privateKey, nonce);
        logger?.LogInformation("SignMusig produced partial signature successfully");
        return Task.FromResult(sig);
    }

    public Task<(ECXOnlyPubKey, SecpSchnorrSignature)> Sign(OutputDescriptor descriptor, uint256 hash, CancellationToken cancellationToken = default)
    {
        var descriptorExtract = descriptor.Extract();
        var descriptorXOnly = descriptorExtract.XOnlyPubKey;
        var signerXOnly = _publicKey.ToXOnlyPubKey();

        if (!descriptorXOnly.ToBytes().SequenceEqual(signerXOnly.ToBytes()))
        {
            var descriptorXOnlyHex = Convert.ToHexString(descriptorXOnly.ToBytes()).ToLowerInvariant();
            var signerXOnlyHex = Convert.ToHexString(signerXOnly.ToBytes()).ToLowerInvariant();
            var signerCompressedHex = Convert.ToHexString(_publicKey.ToBytes()).ToLowerInvariant();
            var descriptorPubKey = descriptorExtract.PubKey;
            var descriptorPubKeyHex = descriptorPubKey is not null
                ? Convert.ToHexString(descriptorPubKey.ToBytes()).ToLowerInvariant()
                : "(null)";

            logger?.LogError(
                "Descriptor does not belong to this wallet. " +
                "Descriptor={Descriptor}, DescriptorXOnly={DescriptorXOnly}, DescriptorPubKey={DescriptorPubKey}, " +
                "SignerXOnly={SignerXOnly}, SignerCompressed={SignerCompressed}",
                descriptor.ToString(), descriptorXOnlyHex, descriptorPubKeyHex,
                signerXOnlyHex, signerCompressedHex);

            throw new InvalidOperationException(
                $"Descriptor does not belong to this wallet. " +
                $"DescriptorXOnly={descriptorXOnlyHex}, SignerXOnly={signerXOnlyHex}, " +
                $"Descriptor={descriptor}");
        }

        if (!privateKey.TrySignBIP340(hash.ToBytes(), null, out var sig))
        {
            throw new InvalidOperationException("Failed to sign data");
        }

        return Task.FromResult((_xOnlyPubKey, sig));
    }

    public Task<MusigPubNonce> GenerateNonces(OutputDescriptor descriptor, MusigContext context,
        string sessionId, CancellationToken cancellationToken = default)
    {
        if (_secNonces.ContainsKey(sessionId))
            throw new InvalidOperationException(
                $"A secret nonce is already stored for sessionId '{sessionId}'. " +
                "Call SignMusig to consume it before generating a fresh nonce for the same session; " +
                "MuSig2 nonce reuse leaks the private key.");
        logger?.LogInformation(
            "GenerateNonces called. Descriptor={Descriptor}, SignerCompressed={SignerCompressed}, SessionId={SessionId}",
            descriptor.ToString(),
            Convert.ToHexString(_publicKey.ToBytes()).ToLowerInvariant(),
            sessionId);
        var nonce = context.GenerateNonce(privateKey);
        if (!_secNonces.TryAdd(sessionId, nonce))
            // Race: another thread populated this sessionId between the ContainsKey check and now.
            // Caller bug — duplicate sessionId from two concurrent callers — but defend against it.
            throw new InvalidOperationException(
                $"A secret nonce was concurrently stored for sessionId '{sessionId}'. " +
                "sessionId must be unique per signing operation.");
        logger?.LogInformation("GenerateNonces produced nonce successfully");
        return Task.FromResult(nonce.CreatePubNonce());
    }
}
