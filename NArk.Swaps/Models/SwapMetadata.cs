namespace NArk.Swaps.Models;

/// <summary>
/// Well-known metadata keys for chain swaps.
/// </summary>
public static class SwapMetadata
{
    public const string Preimage = "preimage";
    public const string EphemeralKey = "ephemeralKey";
    public const string BoltzResponse = "boltzResponse";
    public const string BtcAddress = "btcAddress";
    public const string CrossSigned = "crossSigned";

    /// <summary>
    /// Arkade address (string form) of the refund destination contract. Set the first
    /// time a cooperative refund derives a destination so subsequent poll retries
    /// reuse it instead of deriving fresh contracts and leaking orphan rows into
    /// <c>IContractStorage</c>.
    /// </summary>
    public const string RefundDestination = "refundDestination";

    /// <summary>
    /// Intent transaction ID of the in-flight refund-without-receiver batch intent.
    /// Set by <c>TryRefundWithoutReceiverAsync</c> after submitting the intent so
    /// subsequent poll ticks can query the intent state instead of re-generating it.
    /// </summary>
    public const string RefundIntentTxId = "refundIntentTxId";

    // ── Persistence shim for the ProviderId / Route fields on ArkSwap.
    // These properties don't have dedicated columns on ArkSwapEntity (yet —
    // see issue #79 review), so EfCoreSwapStorage round-trips them through
    // the existing Metadata jsonb column under these well-known keys. Having
    // them as constants keeps the serialization symmetric and reviewable.
    public const string ProviderId = "providerId";
    public const string RouteSourceNetwork = "route.source.network";
    public const string RouteSourceAssetId = "route.source.assetId";
    public const string RouteDestinationNetwork = "route.destination.network";
    public const string RouteDestinationAssetId = "route.destination.assetId";
}