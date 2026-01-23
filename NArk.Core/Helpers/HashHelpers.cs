using System.Text;

namespace NArk.Core.Helpers;

internal static class HashHelpers
{
    internal static byte[] CreateTaggedMessageHash(string tag, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        using var sha = new NBitcoin.Secp256k1.SHA256();
        sha.InitializeTagged(tag);
        sha.Write(bytes);
        return sha.GetHash();
    }

    internal static byte[] CreateTaggedMessageHash(string tag, params byte[][] data)
    {
        using var sha = new NBitcoin.Secp256k1.SHA256();
        sha.InitializeTagged(tag);
        foreach (var bytes in data)
        {
            sha.Write(bytes);
        }
        return sha.GetHash();
    }
}