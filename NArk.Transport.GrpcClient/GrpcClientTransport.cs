using Ark.V1;
using Grpc.Net.Client;

namespace NArk.Transport.GrpcClient;

public partial class GrpcClientTransport : IClientTransport
{

    private readonly ArkService.ArkServiceClient _serviceClient;
    private readonly IndexerService.IndexerServiceClient _indexerServiceClient;

    public GrpcClientTransport(string uri)
    {
        var channel = GrpcChannel.ForAddress(uri);
        _serviceClient = new ArkService.ArkServiceClient(channel);
        _indexerServiceClient = new IndexerService.IndexerServiceClient(channel);
    }
}