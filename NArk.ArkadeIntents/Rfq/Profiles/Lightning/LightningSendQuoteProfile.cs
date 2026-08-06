namespace NArk.ArkadeIntents.Rfq.Profiles.Lightning;

/// <summary>
/// Quote fields of the Lightning send profile — compare-only, never trusted. The values a client
/// may act on are the quote's own binding fields.
/// </summary>
public sealed class LightningSendQuoteProfile
{
    /// <summary>The invoice's payment hash, echoed back.</summary>
    public string? PaymentHash { get; init; }

    /// <summary>
    /// The solver's derivation of the swap contract's address. Compare-only: check it against your
    /// own derivation and refuse to fund on any mismatch.
    /// </summary>
    public string? LockupAddress { get; init; }
}
