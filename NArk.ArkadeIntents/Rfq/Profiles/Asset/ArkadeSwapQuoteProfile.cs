namespace NArk.ArkadeIntents.Rfq.Profiles.Asset;

/// <summary>
/// Quote fields of the <c>arkade:X-&gt;arkade:Y</c> profile. Both are compare-only.
/// </summary>
/// <remarks>
/// The client derives the same offer covenant from the quote's own <c>to_amount</c> plus the two
/// parameters it supplied itself, and funds only its own derivation. A solver that names a different
/// address gets a client that refuses to fund — never one whose deposit is trapped. Both values are
/// published rather than just the address because they are checked against two different things: the
/// address is what the wallet sends to, and the script is what the local covenant compiles to.
/// </remarks>
public sealed class ArkadeSwapQuoteProfile
{
    /// <summary>The solver's derivation of the offer's address.</summary>
    public string? OfferAddress { get; init; }

    /// <summary>The solver's derivation of the offer's scriptPubKey, hex.</summary>
    public string? OfferPkScript { get; init; }
}
