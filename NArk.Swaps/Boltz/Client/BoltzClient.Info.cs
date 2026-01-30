using System.Net.Http.Json;
using NArk.Swaps.Boltz.Models.Info;

namespace NArk.Swaps.Boltz.Client;

public partial class BoltzClient
{
    // Info Endpoints

    /// <summary>
    /// Gets the version of the Boltz API.
    /// </summary>
    /// <returns>The API version information.</returns>
    public virtual async Task<VersionResponse?> GetVersionAsync()
    {
        return await _httpClient.GetFromJsonAsync<VersionResponse>("v2/version");
    }

}
