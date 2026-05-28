using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace NArk.Core.Payments;

/// <summary>
/// Client-side LNURL-pay decoder and callback handler. Resolves both bech32 <c>lnurl1…</c>
/// strings and Lightning Addresses (<c>user@domain</c>) into pay-params, and fetches a BOLT11
/// invoice from a callback URL.
/// <para>Pure consumer helper — no Ark-specific state. Lifted into the SDK so wallet hosts
/// don't each reinvent the same well-known-URL + bech32 + JSON decode dance.</para>
/// </summary>
public class LnurlHelper(HttpClient http)
{
    /// <summary>
    /// Pay-params returned by an LNURL-pay endpoint. Amounts are normalised to satoshis
    /// (the wire format is millisats).
    /// </summary>
    public record LnurlPayParams(
        string Callback,
        long MinSendable,
        long MaxSendable,
        string? Description);

    /// <summary>True if <paramref name="input"/> looks like an LNURL or Lightning Address.</summary>
    public static bool IsLnurl(string input)
    {
        input = input.Trim();
        if (input.StartsWith("lnurl1", StringComparison.OrdinalIgnoreCase))
            return true;
        if (input.StartsWith("lightning:", StringComparison.OrdinalIgnoreCase))
            return true;
        // Lightning Address: user@domain (no spaces, '@' not in first column).
        if (input.Contains('@') && !input.Contains(' ') && input.IndexOf('@') > 0)
            return true;
        return false;
    }

    /// <summary>
    /// Resolves an LNURL or Lightning Address to its pay parameters by fetching the
    /// well-known URL (Lightning Address) or decoded bech32 URL (LNURL).
    /// </summary>
    public async Task<LnurlPayParams> ResolveAsync(string input, CancellationToken cancellationToken = default)
    {
        input = input.Trim();

        string url;
        if (input.StartsWith("lightning:", StringComparison.OrdinalIgnoreCase))
            input = input["lightning:".Length..];

        if (input.Contains('@'))
        {
            // Lightning Address → well-known URL.
            var parts = input.Split('@', 2);
            url = $"https://{parts[1]}/.well-known/lnurlp/{parts[0]}";
        }
        else if (input.StartsWith("lnurl1", StringComparison.OrdinalIgnoreCase))
        {
            url = DecodeLnurl(input);
        }
        else
        {
            throw new ArgumentException("Not a valid LNURL or Lightning Address", nameof(input));
        }

        var response = await http.GetFromJsonAsync<LnurlPayResponse>(url, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Failed to fetch LNURL-pay params");

        if (response.Tag?.ToLowerInvariant() != "payrequest")
            throw new InvalidOperationException($"Expected payRequest, got: {response.Tag}");

        return new LnurlPayParams(
            response.Callback,
            response.MinSendable / 1000, // millisats → sats
            response.MaxSendable / 1000,
            response.Metadata);
    }

    /// <summary>
    /// Fetches a BOLT11 invoice from an LNURL-pay <c>callback</c> URL for the given amount.
    /// </summary>
    public async Task<string> FetchInvoiceAsync(string callback, long amountSats, CancellationToken cancellationToken = default)
    {
        var amountMsat = amountSats * 1000;
        var separator = callback.Contains('?') ? "&" : "?";
        var url = $"{callback}{separator}amount={amountMsat}";

        var response = await http.GetFromJsonAsync<LnurlCallbackResponse>(url, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Failed to fetch invoice from LNURL callback");

        if (!string.IsNullOrEmpty(response.Reason))
            throw new InvalidOperationException($"LNURL error: {response.Reason}");

        return response.Pr ?? throw new InvalidOperationException("No invoice in LNURL response");
    }

    /// <summary>Decodes a bech32 <c>lnurl1…</c> string to its underlying callback URL.</summary>
    public static string DecodeLnurl(string lnurl)
    {
        var encoder = NBitcoin.DataEncoders.Encoders.Bech32("lnurl");
        encoder.StrictLength = false;
        encoder.SquashBytes = true;
        var data = encoder.DecodeDataRaw(lnurl.ToLowerInvariant(), out _);
        return System.Text.Encoding.UTF8.GetString(data);
    }

    private record LnurlPayResponse
    {
        [JsonPropertyName("tag")]
        public string? Tag { get; init; }
        [JsonPropertyName("callback")]
        public string Callback { get; init; } = "";
        [JsonPropertyName("minSendable")]
        public long MinSendable { get; init; }
        [JsonPropertyName("maxSendable")]
        public long MaxSendable { get; init; }
        [JsonPropertyName("metadata")]
        public string? Metadata { get; init; }
    }

    private record LnurlCallbackResponse
    {
        [JsonPropertyName("pr")]
        public string? Pr { get; init; }
        [JsonPropertyName("reason")]
        public string? Reason { get; init; }
    }
}
