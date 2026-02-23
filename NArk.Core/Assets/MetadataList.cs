using System.Security.Cryptography;
using System.Text;

namespace NArk.Core.Assets;

/// <summary>
/// An ordered list of AssetMetadata entries with Merkle hash support.
/// The Merkle tree uses BIP340 tagged hashes with tags "ArkadeAssetLeaf" and "ArkadeAssetBranch".
/// </summary>
public class MetadataList
{
    public const string ArkLeafTag = "ArkadeAssetLeaf";
    public const string ArkBranchTag = "ArkadeAssetBranch";
    public const byte ArkLeafVersion = 0x00;

    public IReadOnlyList<AssetMetadata> Items { get; }

    public MetadataList(IReadOnlyList<AssetMetadata> items)
    {
        Items = items;
    }

    public static MetadataList FromReader(BufferReader reader)
    {
        var count = (int)reader.ReadVarInt();
        var items = new List<AssetMetadata>(count);
        for (var i = 0; i < count; i++)
            items.Add(AssetMetadata.FromReader(reader));
        return new MetadataList(items);
    }

    public static MetadataList FromBytes(byte[] buf)
    {
        if (buf is not { Length: > 0 })
            throw new ArgumentException("missing metadata list");
        var reader = new BufferReader(buf);
        return FromReader(reader);
    }

    public static MetadataList FromString(string hex)
    {
        byte[] buf;
        try { buf = Convert.FromHexString(hex); }
        catch { throw new ArgumentException("invalid metadata list format"); }
        return FromBytes(buf);
    }

    public byte[] Serialize()
    {
        var writer = new BufferWriter();
        SerializeTo(writer);
        return writer.ToBytes();
    }

    public void SerializeTo(BufferWriter writer)
    {
        writer.WriteVarInt((ulong)Items.Count);
        foreach (var item in Items)
            item.SerializeTo(writer);
    }

    /// <summary>
    /// Computes the Merkle root hash of the metadata list using BIP340 tagged hashes.
    /// Leaf: taggedHash("ArkadeAssetLeaf", 0x00 || varslice(key) || varslice(value))
    /// Branch: taggedHash("ArkadeAssetBranch", smaller || larger) where children are sorted lexicographically.
    /// Odd leaves are promoted unchanged.
    /// </summary>
    public byte[] Hash()
    {
        if (Items.Count == 0)
            throw new ArgumentException("missing metadata list");

        var current = Items.Select(ComputeLeafHash).ToList();

        while (current.Count > 1)
        {
            var next = new List<byte[]>();
            for (var i = 0; i < current.Count; i += 2)
            {
                if (i + 1 < current.Count)
                    next.Add(ComputeBranchHash(current[i], current[i + 1]));
                else
                    next.Add(current[i]);
            }
            current = next;
        }

        return current[0];
    }

    private static byte[] ComputeLeafHash(AssetMetadata md)
    {
        var writer = new BufferWriter();
        writer.WriteByte(ArkLeafVersion);
        writer.WriteVarSlice(md.Key);
        writer.WriteVarSlice(md.Value);
        return TaggedHash(ArkLeafTag, writer.ToBytes());
    }

    private static byte[] ComputeBranchHash(byte[] a, byte[] b)
    {
        var (smaller, larger) = CompareBytes(a, b) < 0 ? (a, b) : (b, a);
        return TaggedHash(ArkBranchTag, smaller, larger);
    }

    /// <summary>
    /// BIP340 tagged hash: SHA256(SHA256(tag) || SHA256(tag) || msg1 || msg2 || ...)
    /// </summary>
    internal static byte[] TaggedHash(string tag, params byte[][] data)
    {
        var tagHash = SHA256.HashData(Encoding.UTF8.GetBytes(tag));

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(tagHash);
        sha.AppendData(tagHash);
        foreach (var d in data)
            sha.AppendData(d);
        return sha.GetHashAndReset();
    }

    private static int CompareBytes(byte[] a, byte[] b)
    {
        var len = Math.Min(a.Length, b.Length);
        for (var i = 0; i < len; i++)
        {
            if (a[i] < b[i]) return -1;
            if (a[i] > b[i]) return 1;
        }
        return a.Length.CompareTo(b.Length);
    }

    public override string ToString() => Convert.ToHexString(Serialize()).ToLowerInvariant();
}
