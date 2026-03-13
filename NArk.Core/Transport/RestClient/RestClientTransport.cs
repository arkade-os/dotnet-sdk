using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    public RestClientTransport(string uri)
    {
        _baseUri = uri.TrimEnd('/');
        _http = new HttpClient { BaseAddress = new Uri(_baseUri) };
    }

    public RestClientTransport(HttpClient http)
    {
        _http = http;
        _baseUri = http.BaseAddress?.ToString().TrimEnd('/') ?? "";
    }
}
