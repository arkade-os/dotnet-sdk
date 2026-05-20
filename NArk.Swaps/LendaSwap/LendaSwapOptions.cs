namespace NArk.Swaps.LendaSwap;

/// <summary>
/// Configuration for the LendaSwap provider.
/// </summary>
public class LendaSwapOptions
{
    /// <summary>
    /// Base URL of the LendaSwap API (e.g. "https://api.lendaswap.com").
    /// </summary>
    public required string ApiUrl { get; set; }

    /// <summary>
    /// Publishable API key for authentication. Sent as X-Publishable-Key header.
    /// </summary>
    public string? PublishableKey { get; set; }

    /// <summary>
    /// Optional referral code (lnds_* format) sent as the client-level default
    /// on every swap-create request. Each request DTO can override per-call via
    /// its own <c>ReferralCode</c>. Mirrors the upstream ts-sdk's
    /// <c>referralCode</c> client option (v0.2.34+); the older name
    /// <c>ReflinkCode</c> is retained below as an obsolete alias.
    /// </summary>
    public string? ReferralCode { get; set; }

    /// <summary>
    /// Deprecated alias for <see cref="ReferralCode"/>. Matches the upstream
    /// ts-sdk's deprecation-not-removal policy for <c>withOrgCode</c>/<c>reflink</c>;
    /// writes flow through to <see cref="ReferralCode"/> so existing callers
    /// keep working.
    /// </summary>
    [Obsolete("Use ReferralCode. Kept as an alias for backward compatibility.")]
    public string? ReflinkCode
    {
        get => ReferralCode;
        set => ReferralCode = value;
    }
}
