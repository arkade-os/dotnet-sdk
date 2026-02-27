using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace NArk.Swaps.LendaSwap.Client;

/// <summary>
/// HTTP client for the LendaSwap API.
/// Uses the partial class pattern — endpoint groups are in separate files.
/// </summary>
public partial class LendaSwapClient
{
    protected readonly HttpClient _httpClient;
    protected readonly IOptions<LendaSwapOptions> _options;

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public LendaSwapClient(HttpClient httpClient, IOptions<LendaSwapOptions> options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        _httpClient.BaseAddress = new Uri(options.Value.ApiUrl.TrimEnd('/') + "/");

        if (!string.IsNullOrWhiteSpace(options.Value.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("X-API-Key", options.Value.ApiKey);
        }
    }

    /// <summary>
    /// Posts a value as JSON and deserializes the response.
    /// </summary>
    protected async Task<TReturn> PostAsJsonAsync<T, TReturn>(string uri, T value, CancellationToken ct = default)
    {
        var resp = await _httpClient.PostAsJsonAsync(uri, value, JsonOptions, ct);

        if (resp.IsSuccessStatusCode)
        {
            return (await resp.Content.ReadFromJsonAsync<TReturn>(options: JsonOptions, ct))!;
        }

        var respStr = await resp.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException(respStr, null, resp.StatusCode);
    }
}
