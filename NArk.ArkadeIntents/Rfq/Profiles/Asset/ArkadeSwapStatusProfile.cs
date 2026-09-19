namespace NArk.ArkadeIntents.Rfq.Profiles.Asset;

/// <summary>
/// Status fields of the <c>arkade:X-&gt;arkade:Y</c> profile.
/// </summary>
/// <remarks>
/// The receipt here is a txid rather than a preimage: this class has no hash lock anywhere, so what
/// proves settlement is the transaction that spent the offer — which the client can also read off
/// the chain itself, without asking the solver for it.
/// </remarks>
public sealed class ArkadeSwapStatusProfile
{
    /// <summary>The offer's address, echoed back.</summary>
    public string? OfferAddress { get; init; }

    /// <summary>The transaction that filled the offer, once one exists.</summary>
    public string? FillTxid { get; init; }

    /// <summary>Why the solver stopped, when it did.</summary>
    public string? FailureReason { get; init; }
}
