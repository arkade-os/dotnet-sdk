using Ark.V1;
using NArk.Core.Assets;
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
                var mdList = MetadataList.FromString(response.Metadata);
                metadata = mdList.Items.ToDictionary(m => m.KeyString, m => m.ValueString);
            }
            catch (ArgumentException)
            {
                // If metadata is not valid hex-encoded binary, ignore it
            }
        }

        return new ArkAssetDetails(
            AssetId: response.AssetId,
            Supply: ulong.TryParse(response.Supply, out var supply) ? supply : 0,
            ControlAssetId: string.IsNullOrEmpty(response.ControlAsset) ? null : response.ControlAsset,
            Metadata: metadata);
    }
}
