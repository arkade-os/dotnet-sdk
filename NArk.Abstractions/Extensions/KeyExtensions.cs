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
            return ParseCached(str, network);

        var bytes = Convert.FromHexString(str);
        if (bytes.Length != 32 && bytes.Length != 33)
        {
            throw new ArgumentException("the string must be 32/33 bytes long", nameof(str));
        }

        return ParseCached($"tr({str})", network);
    }

    // NBitcoin's OutputDescriptor.Parse is observed at ~500ms-1s per call on
    // wildcard taproot descriptors (BIP-380 lexer + key-origin + checksum
    // validation in a hot codepath). The same descriptor strings (server,
    // wallet account, per-contract sender/receiver) are re-parsed dozens of
    // times in a single payment-settlement path, accumulating into ~6s
    // GetCoin latencies. Parsed OutputDescriptors are immutable, so caching
    // by (string, network) is safe. Bounded by the number of unique
    // descriptors the wallet ever sees (small).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, string), OutputDescriptor> _descriptorCache = new();

    private static OutputDescriptor ParseCached(string str, Network network)
    {
        var key = (str, network.NetworkSet.CryptoCode + ":" + network.ChainName);
        return _descriptorCache.GetOrAdd(key, _ => OutputDescriptor.Parse(str, network));
    }
}