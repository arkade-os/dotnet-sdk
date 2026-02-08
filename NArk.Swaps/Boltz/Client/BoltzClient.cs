using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using NArk.Swaps.Boltz.Models;

namespace NArk.Swaps.Boltz.Client;

public partial class BoltzClient
{
    protected readonly HttpClient _httpClient;
    protected readonly HttpClient _sidecarHttpClient;
    protected readonly IOptions<BoltzClientOptions> _options;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="BoltzClient"/> class.
    /// </summary>
    /// <param name="httpClient">The HttpClient to use for REST API requests.</param>
    public BoltzClient(HttpClient httpClient, IOptions<BoltzClientOptions> options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _httpClient.BaseAddress = new Uri(options.Value.BoltzUrl);
        _options = options;

        var sidecarUrl = options.Value.SidecarUrl ?? options.Value.BoltzUrl;
        _sidecarHttpClient = new HttpClient { BaseAddress = new Uri(sidecarUrl) };
    }

    /// <summary>
    /// Derives the WebSocket URI from the base HTTP URI.
    /// </summary>
    /// <param name="baseHttpUri">The base HTTP URI of the Boltz API.</param>
    /// <returns>The corresponding WebSocket URI.</returns>
    /// <exception cref="ArgumentNullException">Thrown when baseHttpUri is null.</exception>
    public virtual Uri DeriveWebSocketUri()
    {
        var baseHttpUri = new Uri(_options.Value.WebsocketUrl);

        if (baseHttpUri == null)
        {
            throw new ArgumentNullException(nameof(baseHttpUri), "HttpClient.BaseAddress cannot be null when WebSocket URI is not explicitly provided.");
        }

        var uriBuilder = new UriBuilder(baseHttpUri);
        uriBuilder.Scheme = baseHttpUri.Scheme == "https" ? "wss" : "ws";
        uriBuilder.Port = baseHttpUri.Port;
        var path = uriBuilder.Path.TrimEnd('/');
        uriBuilder.Path = path + "/v2/ws";
        return uriBuilder.Uri;
    }

    /// <summary>
    /// Posts a value as JSON using the shared serialization options.
    /// </summary>
    private Task<TReturn> PostAsJsonAsync<T, TReturn>(string uri, T value, CancellationToken ct = default)
        => PostAsJsonAsync<T, TReturn>(_httpClient, uri, value, ct);

    /// <summary>
    /// Posts a value as JSON to the sidecar API.
    /// </summary>
    protected Task<TReturn> PostToSidecarAsync<T, TReturn>(string uri, T value, CancellationToken ct = default)
        => PostAsJsonAsync<T, TReturn>(_sidecarHttpClient, uri, value, ct);

    private static async Task<TReturn> PostAsJsonAsync<T, TReturn>(HttpClient client, string uri, T value, CancellationToken ct = default)
    {
        var resp = await client.PostAsJsonAsync(uri, value, JsonOptions, ct);

        if (resp.IsSuccessStatusCode)
        {
            return (await resp.Content.ReadFromJsonAsync<TReturn>(options: JsonOptions, ct))!;
        }

        var respStr = await resp.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException(respStr, null, resp.StatusCode);
    }
}
