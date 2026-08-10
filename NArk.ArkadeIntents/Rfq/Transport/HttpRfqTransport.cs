using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NArk.ArkadeIntents.Rfq;

/// <summary>
/// The HTTP transport: <c>POST /v1/swap</c> for quotes, <c>GET /v1/rfq/{rfq_id}</c> for status.
/// </summary>
/// <remarks>
/// The payload is the contract and the HTTP envelope is not, so replies are dispatched on the
/// body's <c>type</c> rather than the status code — a refusal is a refusal whether it arrives as
/// 400 or 422. Unknown members are ignored throughout, per the tolerant-responses rule.
/// </remarks>
public sealed class HttpRfqTransport : IRfqTransport
{
    private readonly HttpClient _http;
    private readonly Uri _baseAddress;

    /// <summary>Creates a transport pointed at one solver.</summary>
    /// <param name="http">The client used for both endpoints.</param>
    /// <param name="baseAddress">
    /// The solver's base address. Optional when <paramref name="http"/> already carries a
    /// <see cref="HttpClient.BaseAddress"/>.
    /// </param>
    public HttpRfqTransport(HttpClient http, Uri? baseAddress = null)
    {
        _http = http;
        var root = baseAddress ?? http.BaseAddress
            ?? throw new ArgumentException(
                "supply a base address, or set one on the HttpClient", nameof(baseAddress));
        // Relative-Uri resolution drops the last path segment of a base without a trailing slash,
        // which would silently post to the wrong path under a solver hosted on a sub-path.
        _baseAddress = root.AbsolutePath.EndsWith('/') ? root : new Uri(root + "/");
    }

    /// <inheritdoc />
    public async Task<RfqQuote<TQuoteProfile>> RequestQuoteAsync<TRequestProfile, TQuoteProfile>(
        RfqRequest<TRequestProfile> request,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync(
            new Uri(_baseAddress, "v1/swap"), request, RfqProtocol.Json, cancellationToken);

        var payload = await ReadPayloadAsync(response, cancellationToken)
            ?? throw new InvalidOperationException(
                $"solver returned {(int)response.StatusCode} with no RFQ payload");

        return RfqProtocol.ExpectQuote<TQuoteProfile>(payload, request.RfqId);
    }

    /// <inheritdoc />
    public async Task<RfqStatus<TStatusProfile>?> GetStatusAsync<TStatusProfile>(
        string rfqId,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.GetAsync(new Uri(_baseAddress, $"v1/rfq/{rfqId}"), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;

        var payload = await ReadPayloadAsync(response, cancellationToken);
        return TypeOf(payload) == "rfq_status"
            ? payload.Deserialize<RfqStatus<TStatusProfile>>(RfqProtocol.Json)
            : null;
    }

    private static async Task<JsonNode?> ReadPayloadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            return JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TypeOf(JsonNode? payload) => payload?["type"]?.GetValue<string>();
}
