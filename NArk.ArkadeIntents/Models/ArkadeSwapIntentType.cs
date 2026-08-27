namespace NArk.ArkadeIntents.Models;

/// <summary>Which direction a non-interactive swap runs in.</summary>
public enum ArkadeSwapIntentType
{
    /// <summary>Deposit Ark BTC, receive an Arkade asset.</summary>
    BtcToAsset,

    /// <summary>Deposit an Arkade asset, receive Ark BTC.</summary>
    AssetToBtc,

    /// <summary>
    /// Deposit Ark BTC, have a BOLT11 invoice paid on the Lightning network. Negotiated by RFQ
    /// rather than by an offer on the stream, and settled against a covenant that pins the refund
    /// to the maker's own address — but from this layer's point of view the same thing: a covenant
    /// VTXO the maker funded and a solver fills without a round trip.
    /// </summary>
    BtcToLightning,

    /// <summary>
    /// Be paid over Lightning and take delivery on Arkade. The mirror of
    /// <see cref="BtcToLightning"/>, with the exposure mirrored too: here the solver funds the
    /// covenant first and is only paid once our claim publishes the preimage, which is why we — not
    /// the solver — choose that secret.
    /// </summary>
    LightningToBtc,

    /// <summary>
    /// Deposit Ark BTC, take delivery on Bitcoin L1. Two contracts on two rails linked by one
    /// secret: the client funds an Arkade covenant, the solver funds an L1 HTLC paying the client,
    /// and the client's L1 claim publishes the preimage the solver needs for the Arkade side.
    /// </summary>
    /// <remarks>
    /// From the Arkade side this behaves exactly like <see cref="BtcToLightning"/> — a covenant the
    /// client funded and the solver fills. What differs is that the delivery leg is a chain this
    /// SDK must watch and spend on itself, rather than a payment network reporting back.
    /// </remarks>
    BtcToOnchain,
}
