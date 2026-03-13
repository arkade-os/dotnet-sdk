using System.Net.Http.Json;
using System.Text.Json;
using NArk.Core.Assets;
using NArk.Core.Transport.Models;

namespace NArk.Transport.RestClient;

public partial class RestClientTransport
{
    public async Task<ArkAssetDetails> GetAssetDetailsAsync(string assetId,
        CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(
            $"/v1/indexer/asset/{assetId}", JsonOpts, cancellationToken);

        Dictionary<string, string>? metadata = null;
        if (json.TryGetProperty("metadata", out var md) && md.ValueKind == JsonValueKind.String)
        {
            var metadataHex = md.GetString();
            if (!string.IsNullOrEmpty(metadataHex))
            {
                try
                {
                    var mdList = MetadataList.FromString(metadataHex);
                    metadata = mdList.Items.ToDictionary(m => m.KeyString, m => m.ValueString);
                }
                catch (ArgumentException) { }
            }
        }

        var supply = json.TryGetProperty("supply", out var s) && ulong.TryParse(s.GetString(), out var sup) ? sup : 0UL;
        var controlAsset = json.TryGetProperty("control_asset", out var ca) ? ca.GetString() : null;
        if (string.IsNullOrEmpty(controlAsset)) controlAsset = null;

        return new ArkAssetDetails(
            AssetId: json.GetProperty("asset_id").GetString()!,
            Supply: supply,
            ControlAssetId: controlAsset,
            Metadata: metadata);
    }
}
