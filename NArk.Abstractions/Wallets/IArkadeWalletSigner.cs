using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NBitcoin.Secp256k1.Musig;

namespace NArk.Abstractions.Wallets;

public interface IArkadeWalletSigner
{
    Task<MusigPartialSignature> SignMusig(
        OutputDescriptor descriptor,
        MusigContext context,
        MusigPrivNonce nonce,
        CancellationToken cancellationToken = default);


    Task<(ECXOnlyPubKey, SecpSchnorrSignature)> Sign(
        OutputDescriptor descriptor,
        uint256 hash,
        CancellationToken cancellationToken = default);

    Task<MusigPrivNonce> GenerateNonces(
        OutputDescriptor descriptor,
        MusigContext context,
        CancellationToken cancellationToken = default
    );
}