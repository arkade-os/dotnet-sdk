namespace NArk.Swaps.Boltz.Models;

public class BoltzClientOptions
{
    /// <summary>
    /// Default referral identifier used when a consumer application does
    /// not configure its own. Boltz uses this to credit the originating
    /// integration for the swap; leaving the SDK-level default in place
    /// means swaps from un-customised integrations are attributed to the
    /// .NET SDK as a whole. Consumer apps that have their own referral
    /// (e.g. the BTCPay plugin uses "btcpay-arkade", standalone wallet
    /// integrations use "arkade-money") should override
    /// <see cref="ReferralId"/> via <c>services.Configure&lt;BoltzClientOptions&gt;</c>.
    /// </summary>
    public const string DefaultReferralId = "arkade-dotnet-sdk";

    public required string BoltzUrl { get; set; }
    public required string WebsocketUrl { get; set; }

    /// <summary>
    /// Referral identifier sent with every Boltz swap-creation request
    /// (Submarine, Reverse, Chain). Defaults to <see cref="DefaultReferralId"/>
    /// (<c>"arkade-dotnet-sdk"</c>); set to <c>null</c> to omit the field
    /// entirely from outgoing requests, or to a per-integration value
    /// (e.g. <c>"btcpay-arkade"</c>, <c>"arkade-money"</c>) issued by
    /// Boltz to claim attribution for that integration.
    /// </summary>
    public string? ReferralId { get; set; } = DefaultReferralId;
}
