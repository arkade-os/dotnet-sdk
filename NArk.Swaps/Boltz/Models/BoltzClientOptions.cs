namespace NArk.Swaps.Boltz.Models;

public class BoltzClientOptions
{
    public required string BoltzUrl { get; set; }
    public required string WebsocketUrl { get; set; }

    /// <summary>
    /// Optional referral identifier sent with every Boltz swap-creation
    /// request (Submarine, Reverse, Chain). Boltz uses this to credit the
    /// originating integration for the swap. Leave null to omit; consumer
    /// apps should set it to the value Boltz issued them (e.g. wallet
    /// integrations use "arkade-money", BTCPay plugin uses "btcpay-arkade").
    /// </summary>
    public string? ReferralId { get; set; }
}
