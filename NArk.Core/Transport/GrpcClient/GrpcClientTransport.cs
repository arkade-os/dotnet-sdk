using Ark.V1;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using NArk.Core.Transport;

namespace NArk.Transport.GrpcClient;

public partial class GrpcClientTransport : IClientTransport
{

    private readonly ArkService.ArkServiceClient _serviceClient;
    private readonly IndexerService.IndexerServiceClient _indexerServiceClient;

    public GrpcClientTransport(string uri)
    {
        var channel = GrpcChannel.ForAddress(uri);
        var invoker = channel.CreateCallInvoker().Intercept(new BuildVersionInterceptor());
        _serviceClient = new ArkService.ArkServiceClient(invoker);
        _indexerServiceClient = new IndexerService.IndexerServiceClient(invoker);
    }
}