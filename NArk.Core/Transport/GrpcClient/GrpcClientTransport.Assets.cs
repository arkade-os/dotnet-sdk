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
        if (response.Metadata.Count > 0)
        {
            metadata = response.Metadata.ToDictionary(m => m.Key, m => m.Value);
        }

        return new ArkAssetDetails(
            AssetId: response.AssetId,
            Supply: ulong.TryParse(response.Supply, out var supply) ? supply : 0,
            ControlAssetId: string.IsNullOrEmpty(response.ControlAsset) ? null : response.ControlAsset,
            Metadata: metadata);
    }
}
