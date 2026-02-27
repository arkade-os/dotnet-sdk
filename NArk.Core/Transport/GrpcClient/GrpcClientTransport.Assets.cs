using System.Text.Json;
using Ark.V1;
using NArk.Core.Transport.Models;

namespace NArk.Transport.GrpcClient;

public partial class GrpcClientTransport
{
    public async Task<ArkAssetDetails> GetAssetDetailsAsync(string assetId, CancellationToken cancellationToken = default)
    {
        var request = new GetAssetRequest { AssetId = assetId };
        var response = await _indexerServiceClient.GetAssetAsync(request, cancellationToken: cancellationToken);

        Dictionary<string, string>? metadata = null;
        if (!string.IsNullOrEmpty(response.Metadata))
        {
            try
            {
                metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(response.Metadata);
            }
            catch (JsonException)
            {
                // If metadata is not valid JSON key-value pairs, ignore it
            }
        }

        return new ArkAssetDetails(
            AssetId: response.AssetId,
            Supply: ulong.TryParse(response.Supply, out var supply) ? supply : 0,
            ControlAssetId: string.IsNullOrEmpty(response.ControlAsset) ? null : response.ControlAsset,
            Metadata: metadata);
    }
}
