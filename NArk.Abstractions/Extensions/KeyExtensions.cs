using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Abstractions.Extensions;

internal static class KeyExtensions
{
    public static ECXOnlyPubKey ToECXOnlyPubKey(this byte[] pubKeyBytes)
    {
        var pubKey = new PubKey(pubKeyBytes);
        return pubKey.ToECXOnlyPubKey();
    }

    private static ECXOnlyPubKey ToECXOnlyPubKey(this PubKey pubKey)
    {
        var xOnly = pubKey.TaprootInternalKey.ToBytes();
        return ECXOnlyPubKey.Create(xOnly);
    }

    public static ECPubKey ToPubKey(this OutputDescriptor descriptor)
    {
        if (descriptor is not OutputDescriptor.Tr trOutputDescriptor)
        {
            throw new ArgumentException("the output descriptor must be tr", nameof(descriptor));
        }

        byte[]? bytes;
        if (trOutputDescriptor.InnerPubkey is PubKeyProvider.Const constPubKeyProvider)
        {
            if (constPubKeyProvider.Xonly)
                throw new ArgumentException("the output descriptor only describe an xonly public key",
                    nameof(descriptor));
            bytes = constPubKeyProvider.Pk.ToBytes();
        }
        else
        {
            bytes = trOutputDescriptor.InnerPubkey.GetPubKey(0, _ => null).ToBytes();
        }

        return ECPubKey.Create(bytes);
    }

    public static ECXOnlyPubKey ToXOnlyPubKey(this OutputDescriptor descriptor)
    {
        if (descriptor is not OutputDescriptor.Tr trOutputDescriptor)
        {
            throw new ArgumentException("the output descriptor must be tr", nameof(descriptor));
        }

        if (trOutputDescriptor.InnerPubkey is PubKeyProvider.Const { Xonly: true } constPubKeyProvider)
            return ECXOnlyPubKey.Create(constPubKeyProvider.Pk.ToBytes()[1..]);

        return descriptor.ToPubKey().ToXOnlyPubKey();
    }

    public static OutputDescriptor ParseOutputDescriptor(string str, Network network)
    {
        if (!HexEncoder.IsWellFormed(str))
            return OutputDescriptor.Parse(str, network);

        var bytes = Convert.FromHexString(str);
        if (bytes.Length != 32 && bytes.Length != 33)
        {
            throw new ArgumentException("the string must be 32/33 bytes long", nameof(str));
        }

        return OutputDescriptor.Parse($"tr({str})", network);
    }
}