using System.Text.Json;
using System.Text.Json.Serialization;
using NArk.Core.Transport;

namespace NArk.Transport.RestClient;

/// <summary>
/// HTTP/REST + SSE transport for arkd.
/// Drop-in replacement for <see cref="GrpcClient.GrpcClientTransport"/> — implements the same
/// <see cref="NArk.Core.Transport.IClientTransport"/> interface using arkd's gRPC-gateway REST API.
///
/// Use this when gRPC is unavailable (e.g., browser WASM, environments behind HTTP-only proxies).
///
/// Registration:
///   services.AddArkRestTransport(config);
/// </summary>
public partial class RestClientTransport : NArk.Core.Transport.IClientTransport
{
    private readonly HttpClient _http;
    private readonly string _baseUri;
    internal readonly DigestHolder _digestHolder = new();

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    public RestClientTransport(string uri)
    {
        _baseUri = uri.TrimEnd('/');
        _http = new HttpClient(new BuildVersionHandler(_digestHolder) { InnerHandler = new HttpClientHandler() })
        {
            BaseAddress = new Uri(_baseUri)
        };
        _http.InjectHeader();
    }

    /// <summary>
    /// Initialises the transport with a caller-supplied <see cref="HttpClient"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This overload is intended for environments where the <see cref="HttpClient"/> is managed externally
    /// (e.g. Blazor WASM browser client, <c>IHttpClientFactory</c>, test fakes).
    /// </para>
    /// <para>
    /// <strong>Limitation:</strong> <c>X-Digest</c> is never sent on outgoing requests and neither
    /// <c>BUILD_VERSION_TOO_OLD</c> nor <c>DIGEST_MISMATCH</c> error responses are detected
    /// automatically. This limitation is fundamental to this overload — if automatic digest/version
    /// checking is required, use the <see cref="RestClientTransport(string)"/> URI-based constructor
    /// instead.
    /// </para>
    /// </remarks>
    public RestClientTransport(HttpClient http)
    {
        _http = http;
        _baseUri = http.BaseAddress?.ToString().TrimEnd('/') ?? "";
        _http.InjectHeader();
    }
}
