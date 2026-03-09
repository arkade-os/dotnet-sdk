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

    public Task<MusigPrivNonce> GenerateNonces(OutputDescriptor descriptor, MusigContext context, CancellationToken cancellationToken = default)
    {
        logger?.LogInformation(
            "GenerateNonces called. Descriptor={Descriptor}, SignerCompressed={SignerCompressed}",
            descriptor.ToString(),
            Convert.ToHexString(_publicKey.ToBytes()).ToLowerInvariant());
        var nonce = context.GenerateNonce(privateKey);
        logger?.LogInformation("GenerateNonces produced nonce successfully");
        return Task.FromResult(nonce);
    }
}
