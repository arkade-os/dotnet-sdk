using System.Globalization;
using System.Text;
using System.Web;

namespace NArk.Abstractions.Payments;

/// <summary>
/// Information extracted from a BIP21 URI or raw payment destination.
/// Use <see cref="ArkBip21.Parse"/> to create from a string,
/// or <see cref="ArkBip21.Create"/> to build a URI.
/// </summary>
public record Bip21PaymentInfo
{
    /// <summary>
    /// On-chain Bitcoin address from the URI path (may be null for Ark-only URIs).
    /// </summary>
    public string? OnchainAddress { get; init; }

    /// <summary>
    /// Ark protocol address from the <c>ark</c> query parameter.
    /// </summary>
    public string? ArkAddress { get; init; }

    /// <summary>
    /// BOLT11 Lightning invoice or LNURL from the <c>lightning</c> query parameter.
    /// </summary>
    public string? Lightning { get; init; }

    /// <summary>
    /// Requested amount in BTC (as specified in BIP21).
    /// Use <see cref="AmountSats"/> for the satoshi equivalent.
    /// </summary>
    public decimal? Amount { get; init; }

    /// <summary>
    /// Requested amount in satoshis (derived from <see cref="Amount"/>).
    /// </summary>
    public ulong? AmountSats => Amount.HasValue
        ? (ulong)Math.Round(Amount.Value * 100_000_000m, 0, MidpointRounding.AwayFromZero)
        : null;

    /// <summary>
    /// Asset ID from the <c>asset</c> query parameter. Forces Ark-only delivery.
    /// </summary>
    public string? AssetId { get; init; }

    /// <summary>
    /// All query parameters (including ark, lightning, amount, asset).
    /// </summary>
    public IReadOnlyDictionary<string, string> QueryParams { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Determines the best payment method based on available fields.
    /// Priority: Ark (if ark address or asset present) → Lightning → Chain swap.
    /// </summary>
    public ArkPaymentMethod PreferredMethod =>
        ArkAddress is not null || AssetId is not null ? ArkPaymentMethod.ArkSend :
        Lightning is not null ? ArkPaymentMethod.SubmarineSwap :
        OnchainAddress is not null ? ArkPaymentMethod.ChainSwap :
        ArkPaymentMethod.ArkSend;

    /// <summary>
    /// The best recipient address based on <see cref="PreferredMethod"/>.
    /// </summary>
    public string? Recipient => PreferredMethod switch
    {
        ArkPaymentMethod.ArkSend => ArkAddress ?? OnchainAddress,
        ArkPaymentMethod.SubmarineSwap => Lightning,
        ArkPaymentMethod.ChainSwap => OnchainAddress,
        ArkPaymentMethod.CollaborativeExit => OnchainAddress,
        _ => null
    };
}

/// <summary>
/// Builds and parses BIP21 URIs with Ark protocol extensions.
/// <para>
/// <b>Building:</b> <c>ArkBip21.Create().WithArkAddress("tark1...").WithAmount(0.001m).Build()</c>
/// </para>
/// <para>
/// <b>Parsing:</b> <c>ArkBip21.Parse("bitcoin:addr?ark=tark1...")</c> — also handles raw
/// Ark addresses, Lightning invoices, and Bitcoin addresses.
/// </para>
/// </summary>
public class ArkBip21
{
    private string? _onchainAddress;
    private string? _arkAddress;
    private string? _lightning;
    private decimal? _amount;
    private string? _assetId;
    private readonly Dictionary<string, string> _customParameters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a new builder instance.
    /// </summary>
    public static ArkBip21 Create() => new();

    /// <summary>
    /// Sets the Bitcoin on-chain address (optional, placed in URI path).
    /// </summary>
    public ArkBip21 WithOnchainAddress(string? address)
    {
        _onchainAddress = address;
        return this;
    }

    /// <summary>
    /// Sets the Ark protocol address (placed in <c>ark</c> query parameter).
    /// </summary>
    public ArkBip21 WithArkAddress(string arkAddress)
    {
        if (string.IsNullOrWhiteSpace(arkAddress))
            throw new ArgumentException("Ark address cannot be null or empty.", nameof(arkAddress));
        _arkAddress = arkAddress;
        return this;
    }

    /// <summary>
    /// Sets the Lightning invoice or LNURL (placed in <c>lightning</c> query parameter).
    /// </summary>
    public ArkBip21 WithLightning(string? lightning)
    {
        _lightning = lightning;
        return this;
    }

    /// <summary>
    /// Sets the payment amount in BTC.
    /// </summary>
    public ArkBip21 WithAmount(decimal? amount)
    {
        _amount = amount;
        return this;
    }

    /// <summary>
    /// Sets the asset ID (placed in <c>asset</c> query parameter, forces Ark-only).
    /// </summary>
    public ArkBip21 WithAssetId(string? assetId)
    {
        _assetId = assetId;
        return this;
    }

    /// <summary>
    /// Adds a custom query parameter.
    /// </summary>
    public ArkBip21 WithCustomParameter(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Parameter key cannot be null or empty.", nameof(key));
        _customParameters[key] = value;
        return this;
    }

    /// <summary>
    /// Builds the BIP21 URI string.
    /// Format: <c>bitcoin:[onchain_address]?amount=X&amp;ark=Y&amp;lightning=Z</c>
    /// </summary>
    /// <exception cref="InvalidOperationException">No address was set (need at least ark or onchain).</exception>
    public string Build()
    {
        if (string.IsNullOrWhiteSpace(_arkAddress) && string.IsNullOrWhiteSpace(_onchainAddress))
            throw new InvalidOperationException(
                "At least one address is required. Call WithArkAddress() or WithOnchainAddress() before Build().");

        var sb = new StringBuilder("bitcoin:");
        sb.Append(_onchainAddress ?? string.Empty);

        var parameters = new List<string>();

        if (_amount.HasValue)
            parameters.Add($"amount={_amount.Value.ToString(CultureInfo.InvariantCulture)}");

        // Ark address — bech32m is URL-safe, no encoding needed
        if (!string.IsNullOrWhiteSpace(_arkAddress))
            parameters.Add($"ark={_arkAddress}");

        if (!string.IsNullOrWhiteSpace(_lightning))
            parameters.Add($"lightning={HttpUtility.UrlEncode(_lightning)}");

        if (!string.IsNullOrWhiteSpace(_assetId))
            parameters.Add($"asset={HttpUtility.UrlEncode(_assetId)}");

        foreach (var (key, value) in _customParameters)
        {
            if (key is "amount" or "ark" or "lightning" or "asset") continue;
            parameters.Add($"{HttpUtility.UrlEncode(key)}={HttpUtility.UrlEncode(value)}");
        }

        if (parameters.Count > 0)
        {
            sb.Append('?');
            sb.Append(string.Join("&", parameters));
        }

        return sb.ToString();
    }

    // ── Parsing ────────────────────────────────────────────────────────

    /// <summary>
    /// Parses any payment destination: BIP21 URI, Ark address, Lightning invoice, or Bitcoin address.
    /// </summary>
    /// <returns>Parsed payment info, or null if the input is unrecognized.</returns>
    public static Bip21PaymentInfo? Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        input = input.Trim();

        if (input.StartsWith("bitcoin:", StringComparison.OrdinalIgnoreCase))
            return ParseBip21Uri(input);

        if (IsLightningInvoice(input))
            return new Bip21PaymentInfo { Lightning = input };

        if (IsArkAddress(input))
            return new Bip21PaymentInfo { ArkAddress = input };

        if (IsBitcoinAddress(input))
            return new Bip21PaymentInfo { OnchainAddress = input };

        return null;
    }

    /// <summary>
    /// Parses a BIP21 URI. Throws <see cref="FormatException"/> for invalid URIs.
    /// Use <see cref="Parse"/> for lenient parsing that returns null on failure.
    /// </summary>
    /// <exception cref="FormatException">The URI is not a valid BIP21 URI.</exception>
    public static Bip21PaymentInfo ParseStrict(string bip21Uri)
    {
        if (string.IsNullOrWhiteSpace(bip21Uri))
            throw new ArgumentException("BIP21 URI cannot be null or empty.", nameof(bip21Uri));

        if (!bip21Uri.StartsWith("bitcoin:", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Invalid BIP21 URI: must start with 'bitcoin:'.");

        return ParseBip21Uri(bip21Uri)
            ?? throw new FormatException($"Failed to parse BIP21 URI: {bip21Uri}");
    }

    private static Bip21PaymentInfo? ParseBip21Uri(string uri)
    {
        try
        {
            var parsed = new Uri(uri);
            var onchainAddress = parsed.AbsolutePath.TrimStart('/');
            var query = HttpUtility.ParseQueryString(parsed.Query);

            decimal? amount = null;
            if (query["amount"] is { } amountStr &&
                decimal.TryParse(amountStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var btcAmount))
            {
                amount = btcAmount;
            }

            var arkAddress = query["ark"];
            var lightning = query["lightning"];
            var assetId = query["asset"];

            // Preserve all query params for extensibility
            var queryParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string? key in query.AllKeys)
            {
                if (key is not null && query[key] is { } value)
                    queryParams[key] = value;
            }

            return new Bip21PaymentInfo
            {
                OnchainAddress = string.IsNullOrWhiteSpace(onchainAddress) ? null : onchainAddress,
                ArkAddress = string.IsNullOrWhiteSpace(arkAddress) ? null : arkAddress,
                Lightning = string.IsNullOrWhiteSpace(lightning) ? null : lightning,
                Amount = amount,
                AssetId = string.IsNullOrWhiteSpace(assetId) ? null : assetId,
                QueryParams = queryParams
            };
        }
        catch
        {
            return null;
        }
    }

    // ── Address detection helpers ───────────────────────────────────────

    private static bool IsLightningInvoice(string input) =>
        input.StartsWith("lnbc", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("lntb", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("lnbcrt", StringComparison.OrdinalIgnoreCase);

    private static bool IsArkAddress(string input) =>
        input.StartsWith("ark1", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("tark1", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Basic Bitcoin address detection. Checks prefix + length to avoid false positives
    /// on short strings like "main" or "3.14".
    /// </summary>
    private static bool IsBitcoinAddress(string input)
    {
        // Bech32/bech32m (segwit v0/v1): bc1/tb1/bcrt1 + 39-62 chars
        if (input.StartsWith("bc1", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("tb1", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("bcrt1", StringComparison.OrdinalIgnoreCase))
            return input.Length >= 42 && input.Length <= 90;

        // Legacy P2PKH/P2SH: starts with 1/3 (mainnet), m/n/2 (testnet)
        // Base58Check addresses are 25-34 chars, but encoded as 26-35 characters
        if (input.Length < 26 || input.Length > 35)
            return false;

        return input[0] is '1' or '3' or 'm' or 'n' or '2';
    }
}
