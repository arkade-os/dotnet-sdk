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
    /// Optional referral/reflink code (lnds_* format) attached to swap requests.
    /// </summary>
    public string? ReflinkCode { get; set; }
}
