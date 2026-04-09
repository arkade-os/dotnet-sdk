using System.Globalization;
using NArk.Abstractions.VTXOs;

namespace NArk.Abstractions.Payments;

/// <summary>
/// Parsed result from a BIP21 URI or raw payment destination.
/// Use <see cref="Bip21Parser.Parse"/> to create instances.
/// </summary>
public record Bip21PaymentInfo
{
    /// <summary>
    /// On-chain Bitcoin address from the URI path (may be null for Lightning-only).
    /// </summary>
    public string? OnchainAddress { get; init; }

    /// <summary>
    /// Ark protocol address from the <c>ark</c> query parameter.
    /// </summary>
    public string? ArkAddress { get; init; }

    /// <summary>
    /// BOLT11 Lightning invoice from the <c>lightning</c> query parameter.
    /// </summary>
    public string? LightningInvoice { get; init; }

    /// <summary>
    /// Requested amount in satoshis (converted from BIP21 BTC amount).
    /// </summary>
    public ulong? AmountSats { get; init; }

    /// <summary>
    /// Asset ID if present — forces Ark-only delivery.
    /// </summary>
    public string? AssetId { get; init; }

    /// <summary>
    /// Raw query parameters for extensibility.
    /// </summary>
    public IReadOnlyDictionary<string, string> QueryParams { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Determines the best payment method based on available fields.
    /// Priority: Ark (if ark address or asset present) → Lightning → Chain swap / CollaborativeExit.
    /// </summary>
    public ArkPaymentMethod PreferredMethod =>
        ArkAddress is not null || AssetId is not null ? ArkPaymentMethod.ArkSend :
        LightningInvoice is not null ? ArkPaymentMethod.SubmarineSwap :
        OnchainAddress is not null ? ArkPaymentMethod.ChainSwap :
        ArkPaymentMethod.ArkSend;

    /// <summary>
    /// The best recipient address based on <see cref="PreferredMethod"/>.
    /// </summary>
    public string? Recipient => PreferredMethod switch
    {
        ArkPaymentMethod.ArkSend => ArkAddress ?? OnchainAddress,
        ArkPaymentMethod.SubmarineSwap => LightningInvoice,
        ArkPaymentMethod.ChainSwap => OnchainAddress,
        ArkPaymentMethod.CollaborativeExit => OnchainAddress,
        _ => null
    };
}

/// <summary>
/// Parses BIP21 URIs and raw payment destinations (Ark addresses, Lightning invoices,
/// Bitcoin addresses) into a unified <see cref="Bip21PaymentInfo"/>.
/// </summary>
public static class Bip21Parser
{
    /// <summary>
    /// Parse any payment destination string: BIP21 URI, Ark address, Lightning invoice, or Bitcoin address.
    /// </summary>
    /// <returns>Parsed payment info, or null if the input is unrecognized.</returns>
    public static Bip21PaymentInfo? Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        input = input.Trim();

        // BIP21 URI
        if (input.StartsWith("bitcoin:", StringComparison.OrdinalIgnoreCase))
            return ParseBip21(input);

        // Lightning invoice (BOLT11)
        if (IsLightningInvoice(input))
            return new Bip21PaymentInfo { LightningInvoice = input };

        // Ark address
        if (IsArkAddress(input))
            return new Bip21PaymentInfo { ArkAddress = input };

        // On-chain Bitcoin address (bech32, bech32m, legacy)
        if (IsBitcoinAddress(input))
            return new Bip21PaymentInfo { OnchainAddress = input };

        return null;
    }

    private static Bip21PaymentInfo? ParseBip21(string uri)
    {
        try
        {
            var withoutScheme = uri["bitcoin:".Length..];
            var qIdx = withoutScheme.IndexOf('?');
            var address = qIdx >= 0 ? withoutScheme[..qIdx] : withoutScheme;
            var queryParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (qIdx >= 0)
            {
                var queryString = withoutScheme[(qIdx + 1)..];
                foreach (var pair in queryString.Split('&'))
                {
                    var eqIdx = pair.IndexOf('=');
                    if (eqIdx > 0)
                    {
                        queryParams[Uri.UnescapeDataString(pair[..eqIdx])] =
                            Uri.UnescapeDataString(pair[(eqIdx + 1)..]);
                    }
                }
            }

            ulong? amountSats = null;
            if (queryParams.TryGetValue("amount", out var amountStr) &&
                decimal.TryParse(amountStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var btcAmount))
            {
                amountSats = (ulong)(btcAmount * 100_000_000m);
            }

            queryParams.TryGetValue("ark", out var arkAddress);
            queryParams.TryGetValue("lightning", out var lightningInvoice);
            queryParams.TryGetValue("asset", out var assetId);

            return new Bip21PaymentInfo
            {
                OnchainAddress = string.IsNullOrWhiteSpace(address) ? null : address,
                ArkAddress = string.IsNullOrWhiteSpace(arkAddress) ? null : arkAddress,
                LightningInvoice = string.IsNullOrWhiteSpace(lightningInvoice) ? null : lightningInvoice,
                AmountSats = amountSats,
                AssetId = string.IsNullOrWhiteSpace(assetId) ? null : assetId,
                QueryParams = queryParams
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool IsLightningInvoice(string input) =>
        input.StartsWith("lnbc", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("lntb", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("lnbcrt", StringComparison.OrdinalIgnoreCase);

    private static bool IsArkAddress(string input) =>
        input.StartsWith("ark1", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("tark1", StringComparison.OrdinalIgnoreCase);

    private static bool IsBitcoinAddress(string input) =>
        input.StartsWith("bc1", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("tb1", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("bcrt1", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("1") || input.StartsWith("3") ||
        input.StartsWith("m") || input.StartsWith("n") || input.StartsWith("2");
}
