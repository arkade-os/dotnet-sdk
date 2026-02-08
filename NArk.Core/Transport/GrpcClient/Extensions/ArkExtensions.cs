using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Transport.GrpcClient.Extensions;

internal static class ArkExtensions
{
    public static ECXOnlyPubKey ToECXOnlyPubKey(this string pubKeyHex)
    {
        var pubKey = new PubKey(pubKeyHex);
        return pubKey.ToECXOnlyPubKey();
    }

    public static ECXOnlyPubKey ToECXOnlyPubKey(this PubKey pubKey)
    {
        var xOnly = pubKey.TaprootInternalKey.ToBytes();
        return ECXOnlyPubKey.Create(xOnly);
    }
}