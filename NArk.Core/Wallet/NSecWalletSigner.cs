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

    public Task<MusigPartialSignature> SignMusig(OutputDescriptor descriptor, MusigContext context, MusigPrivNonce nonce,
        CancellationToken cancellationToken = default)
    {
        logger?.LogInformation(
            "SignMusig called. Descriptor={Descriptor}, SignerCompressed={SignerCompressed}",
            descriptor.ToString(),
            Convert.ToHexString(_publicKey.ToBytes()).ToLowerInvariant());
        var key = GetParityMatchedPrivateKey(descriptor);
        var sig = context.Sign(key, nonce);
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

    public Task<MusigPrivNonce> GenerateNonces(OutputDescriptor descriptor, MusigContext context, CancellationToken cancellationToken = default)
    {
        logger?.LogInformation(
            "GenerateNonces called. Descriptor={Descriptor}, SignerCompressed={SignerCompressed}",
            descriptor.ToString(),
            Convert.ToHexString(_publicKey.ToBytes()).ToLowerInvariant());
        var key = GetParityMatchedPrivateKey(descriptor);
        var nonce = context.GenerateNonce(key);
        logger?.LogInformation("GenerateNonces produced nonce successfully");
        return Task.FromResult(nonce);
    }

    /// <summary>
    /// Returns the private key, negated if necessary so its pubkey parity matches the descriptor's pubkey.
    /// MuSig2 contexts are built with the descriptor's pubkey (which may have lost parity through tr() serialization).
    /// The private key must produce a pubkey matching the one in the context.
    /// </summary>
    private ECPrivKey GetParityMatchedPrivateKey(OutputDescriptor descriptor)
    {
        var descriptorPubKey = descriptor.ToPubKey();
        var descriptorPubKeyHex = Convert.ToHexString(descriptorPubKey.ToBytes()).ToLowerInvariant();
        var signerPubKeyHex = Convert.ToHexString(_publicKey.ToBytes()).ToLowerInvariant();

        logger?.LogInformation(
            "GetParityMatchedPrivateKey: DescriptorPubKey={DescriptorPubKey}, SignerPubKey={SignerPubKey}, Match={Match}",
            descriptorPubKeyHex, signerPubKeyHex, _publicKey == descriptorPubKey);

        if (_publicKey == descriptorPubKey)
        {
            logger?.LogInformation("Parity already matches, using original private key");
            return privateKey;
        }

        // X-only keys must match — only parity should differ
        var descriptorXOnly = descriptorPubKey.ToXOnlyPubKey();
        var xOnlyMatch = descriptorXOnly.ToBytes().SequenceEqual(_xOnlyPubKey.ToBytes());

        logger?.LogInformation(
            "Parity mismatch detected. DescriptorXOnly={DescriptorXOnly}, SignerXOnly={SignerXOnly}, XOnlyMatch={XOnlyMatch}",
            Convert.ToHexString(descriptorXOnly.ToBytes()).ToLowerInvariant(),
            Convert.ToHexString(_xOnlyPubKey.ToBytes()).ToLowerInvariant(),
            xOnlyMatch);

        if (!xOnlyMatch)
            throw new InvalidOperationException(
                $"Descriptor x-only key does not match wallet key. " +
                $"DescriptorXOnly={Convert.ToHexString(descriptorXOnly.ToBytes()).ToLowerInvariant()}, " +
                $"SignerXOnly={Convert.ToHexString(_xOnlyPubKey.ToBytes()).ToLowerInvariant()}");

        // Negate the private key: d' = order - d
        // This flips the pubkey parity to match the descriptor
        Span<byte> privBytes = stackalloc byte[32];
        privateKey.WriteToSpan(privBytes);
        var order = new System.Numerics.BigInteger(
            stackalloc byte[] {
                0x00, // unsigned
                0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
                0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFE,
                0xBA, 0xAE, 0xDC, 0xE6, 0xAF, 0x48, 0xA0, 0x3B,
                0xBF, 0xD2, 0x5E, 0x8C, 0xD0, 0x36, 0x41, 0x41
            }, isUnsigned: true, isBigEndian: true);
        var d = new System.Numerics.BigInteger(privBytes, isUnsigned: true, isBigEndian: true);
        var negD = order - d;
        Span<byte> negBytes = stackalloc byte[32];
        negD.TryWriteBytes(negBytes, out _, isUnsigned: true, isBigEndian: true);

        var negatedKey = ECPrivKey.Create(negBytes);
        var negatedPubKey = negatedKey.CreatePubKey();

        logger?.LogInformation(
            "Negated private key. NegatedPubKey={NegatedPubKey}, MatchesDescriptor={MatchesDescriptor}",
            Convert.ToHexString(negatedPubKey.ToBytes()).ToLowerInvariant(),
            negatedPubKey == descriptorPubKey);

        return negatedKey;
    }
}
