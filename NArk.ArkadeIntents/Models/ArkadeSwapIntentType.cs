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
}
