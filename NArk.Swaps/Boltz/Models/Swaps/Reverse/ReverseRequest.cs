using System.Text.Json.Serialization;

namespace NArk.Swaps.Boltz.Models.Swaps.Reverse;

// NonInteractiveClaimRequest lives one namespace up: it is shared with chain swaps.

public class ReverseRequest
{
    [JsonPropertyName("from")]
    public required string From { get; set; } // e.g., "LNBTC"

    [JsonPropertyName("to")]
    public required string To { get; set; } // e.g., "BTC"

    [JsonPropertyName("onchainAddress")]
    public string? OnchainAddress { get; set; }

    [JsonPropertyName("onchainAmount")]
    public long? OnchainAmount { get; set; }

    [JsonPropertyName("invoiceAmount")]
    public long? InvoiceAmount { get; set; }

    [JsonPropertyName("preimageHash")]
    public required string PreimageHash { get; set; }

    [JsonPropertyName("claimPublicKey")]
    public string? ClaimPublicKey { get; set; } // For Taproot

    [JsonPropertyName("referralId")]
    public string? ReferralId { get; set; }

    [JsonPropertyName("acceptZeroConf")]
    public bool? AcceptZeroConf { get; set; }

    [JsonPropertyName("invoiceExpiry")]
    public int? InvoiceExpirySeconds { get; set; }
    [JsonPropertyName("description")]
    public string? Description { get; set; }
    [JsonPropertyName("descriptionHash")]
    public string? DescriptionHash { get; set; }

    /// <summary>
    /// Opt into a non-interactive claim leaf on the VHTLC. Omitted from the wire when
    /// null, so existing callers keep getting the plain six-leaf swap.
    /// </summary>
    [JsonPropertyName("nonInteractiveClaim")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NonInteractiveClaimRequest? NonInteractiveClaim { get; set; }

}
