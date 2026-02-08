using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Abstractions.Extensions;

public static class OutputDescriptorHelpers
{
    public record OutputDescriptorMetadata(
        BitcoinExtPubKey? AccountXpub,
        RootedKeyPath? AccountPath,
        KeyPath? DerivationPath,
        KeyPath? FullPath,
        ECPubKey? PubKey,
        ECXOnlyPubKey XOnlyPubKey
    )
    {
    };

    public static OutputDescriptorMetadata Extract(this OutputDescriptor descriptor)
    {
        if (descriptor is not OutputDescriptor.Tr trOutputDescriptor)
        {
            throw new ArgumentException("the output descriptor must be tr", nameof(descriptor));
        }

        switch (trOutputDescriptor.InnerPubkey)
        {
            // bug in nbitcoin where it always returns xonly:true for tr descriptors
            // case PubKeyProvider.Const { Xonly: true } @const:
            //     return new OutputDescriptorMetadata(null, null, null, null, null,
            //         ECXOnlyPubKey.Create(@const.Pk.TaprootInternalKey.ToBytes()));
            case PubKeyProvider.Const @const:
                var pk = ECPubKey.Create(@const.Pk.ToBytes());
                return new OutputDescriptorMetadata(null, null, null, null, pk, pk.ToXOnlyPubKey());
            case PubKeyProvider.Origin { Inner: PubKeyProvider.HD hd } xx:

                var path = KeyPath.Parse(xx.KeyOriginInfo.KeyPath + "/" + hd.Path);
                var pubKey = ECPubKey.Create(
                    hd.GetPubKey(0, _ => null, out _).ToBytes());
                return new OutputDescriptorMetadata(hd.Extkey, xx.KeyOriginInfo, hd.Path, path, pubKey,
                    pubKey.ToXOnlyPubKey());
            default:
                throw new ArgumentOutOfRangeException();
        }
    }
}