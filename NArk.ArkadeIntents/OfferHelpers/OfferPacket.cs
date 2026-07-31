using NArk.ArkadeIntents.Models;
using NArk.Core.Assets;

namespace NArk.ArkadeIntents.Services;

/// <summary>
/// The Arkade offer as an <see cref="IExtensionPacket"/> (type
/// <see cref="OfferCodec.OfferPacketType"/> = 0x03), so it rides inside the funding transaction's
/// Extension OP_RETURN — where the solver reads it to discover and fill the swap.
/// </summary>
public sealed class OfferPacket : IExtensionPacket
{
    private readonly byte[] _payload;

    /// <inheritdoc />
    public byte PacketType => OfferCodec.OfferPacketType;

    private OfferPacket(byte[] payload) => _payload = payload;

    /// <summary>Wrap an already-serialized offer payload (e.g. from <see cref="CreatedOffer.Payload"/>).</summary>
    public static OfferPacket FromPayload(byte[] payload) => new(payload);

    /// <summary>Serialize <paramref name="offer"/> and wrap it as a packet.</summary>
    public static OfferPacket FromOffer(Offer offer) => new(OfferCodec.Encode(offer));

    /// <summary>Parse the packet payload back into an <see cref="Offer"/>.</summary>
    public Offer ToOffer() => OfferCodec.Decode(_payload);

    /// <inheritdoc />
    public byte[] SerializePacketData() => _payload;
}
