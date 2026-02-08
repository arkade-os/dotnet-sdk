using NArk.Abstractions.Helpers;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Abstractions.Extensions;

public static class KeyExtensions
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
        return descriptor.Extract().PubKey ?? throw new ArgumentException("the output descriptor does not contain a pubkey", nameof(descriptor));
    }

    public static ECXOnlyPubKey ToXOnlyPubKey(this OutputDescriptor descriptor)
    {
        return descriptor.Extract().XOnlyPubKey ?? throw new ArgumentException("the output descriptor does not contain an xonly pubkey", nameof(descriptor));
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