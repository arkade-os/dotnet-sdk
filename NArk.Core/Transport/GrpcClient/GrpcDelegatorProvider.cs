using Fulmine.V1;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using NArk.Abstractions.Services;
using NArk.Core.Transport;

namespace NArk.Transport.GrpcClient;

public class GrpcDelegatorProvider : IDelegatorProvider
{
    private readonly DelegatorService.DelegatorServiceClient _client;
    private readonly DigestHolder _digestHolder = new();

    public GrpcDelegatorProvider(string uri)
    {
        var channel = GrpcChannel.ForAddress(uri);
        var invoker = channel.CreateCallInvoker().Intercept(new BuildVersionInterceptor(_digestHolder));
        _client = new DelegatorService.DelegatorServiceClient(invoker);
    }

    public async Task<DelegatorInfo> GetDelegatorInfoAsync(CancellationToken cancellationToken = default)
    {
        var response = await _client.GetDelegatorInfoAsync(
            new GetDelegatorInfoRequest(),
            cancellationToken: cancellationToken);
        return new DelegatorInfo(response.Pubkey, response.Fee, response.DelegatorAddress);
    }

    public async Task DelegateAsync(
        string intentMessage,
        string intentProof,
        string[] forfeitTxs,
        bool rejectReplace = false,
        CancellationToken cancellationToken = default)
    {
        var request = new DelegateRequest
        {
            Intent = new Intent
            {
                Message = intentMessage,
                Proof = intentProof
            },
            RejectReplace = rejectReplace
        };
        request.ForfeitTxs.AddRange(forfeitTxs);

        await _client.DelegateAsync(request, cancellationToken: cancellationToken);
    }
}
