using System.Buffers.Binary;
using NArk.Core.Assets;
using NArk.ArkadeIntents.Models;

namespace NArk.ArkadeIntents.Services;

/// <summary>
/// Wire format for Arkade <see cref="Offer"/>s: an Extension packet payload of
/// <c>[type:1B][length:2B BE][value]</c> TLV records. Byte-compatible with the arkade wallet's
/// <c>encodeOffer</c>/<c>decodeOffer</c>.
/// </summary>
public static class OfferCodec
{
    /// <summary>Extension packet type tag for Arkade offers.</summary>
    public const byte OfferPacketType = 0x03;

    private const byte TSwapPkScript = 0x01;
    private const byte TWantAmount = 0x02;
    private const byte TWantAsset = 0x03;
    private const byte TMakerPkScript = 0x05;
    private const byte TMakerPublicKey = 0x07;
    private const byte TEmulatorPubkey = 0x08;
    private const byte TOfferAsset = 0x0b;

    private static readonly IReadOnlySet<byte> KnownTypes = new HashSet<byte>
    {
        TSwapPkScript, TWantAmount, TWantAsset, TMakerPkScript, TMakerPublicKey, TEmulatorPubkey, TOfferAsset,
    };

    /// <summary>Serialize an offer to its TLV packet payload.</summary>
    public static byte[] Encode(Offer offer)
    {
        var amount = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(amount, (ulong)offer.WantAmount);

        var records = new List<byte[]>
        {
            Tlv(TSwapPkScript, offer.SwapPkScript),
            Tlv(TWantAmount, amount),
        };
        if (offer.WantAsset is { } wantAsset) records.Add(Tlv(TWantAsset, wantAsset.Serialize()));
        if (offer.OfferAsset is { } offerAsset) records.Add(Tlv(TOfferAsset, offerAsset.Serialize()));
        records.Add(Tlv(TMakerPkScript, offer.MakerPkScript));
        records.Add(Tlv(TMakerPublicKey, offer.MakerPublicKey));
        records.Add(Tlv(TEmulatorPubkey, offer.EmulatorPubkey));

        return records.SelectMany(r => r).ToArray();
    }

    /// <summary>Parse a TLV packet payload back into an offer. Throws on malformed or unknown records.</summary>
    public static Offer Decode(byte[] data)
    {
        var fields = new Dictionary<byte, byte[]>();
        var offset = 0;
        while (offset < data.Length)
        {
            if (offset + 3 > data.Length)
                throw new FormatException("truncated TLV header");
            var type = data[offset];
            var length = (data[offset + 1] << 8) | data[offset + 2];
            offset += 3;
            if (offset + length > data.Length)
                throw new FormatException($"truncated TLV value for type 0x{type:x2}");
            if (!KnownTypes.Contains(type))
                throw new FormatException($"unknown TLV type: 0x{type:x2}");
            fields[type] = data[offset..(offset + length)];
            offset += length;
        }

        byte[] Need(byte type, int? expectedLength = null)
        {
            if (!fields.TryGetValue(type, out var value))
                throw new FormatException($"missing TLV record 0x{type:x2}");
            if (expectedLength is { } len && value.Length != len)
                throw new FormatException($"TLV record 0x{type:x2} has length {value.Length}, expected {len}");
            return value;
        }

        return new Offer
        {
            SwapPkScript = Need(TSwapPkScript),
            WantAmount = (long)BinaryPrimitives.ReadUInt64BigEndian(Need(TWantAmount, 8)),
            WantAsset = fields.TryGetValue(TWantAsset, out var wa) ? AssetId.FromBytes(wa) : null,
            OfferAsset = fields.TryGetValue(TOfferAsset, out var oa) ? AssetId.FromBytes(oa) : null,
            MakerPkScript = Need(TMakerPkScript),
            MakerPublicKey = Need(TMakerPublicKey),
            EmulatorPubkey = Need(TEmulatorPubkey),
        };
    }

    private static byte[] Tlv(byte type, byte[] value)
    {
        if (value.Length > 0xffff)
            throw new ArgumentException($"TLV value for type 0x{type:x2} exceeds 65535 bytes");
        var record = new byte[3 + value.Length];
        record[0] = type;
        record[1] = (byte)((value.Length >> 8) & 0xff);
        record[2] = (byte)(value.Length & 0xff);
        value.CopyTo(record, 3);
        return record;
    }
}
