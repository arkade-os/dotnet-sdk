using Fulmine.V1;
using Grpc.Core;
using NArk.Core.Services;

namespace NArk.Delegator;

/// <summary>
/// gRPC + REST endpoint for the Arkade delegator service. Translates wire requests onto
/// <see cref="DelegateeService"/> and maps domain failures to gRPC status codes.
/// </summary>
public class DelegatorGrpcService(DelegateeService delegatee) : DelegatorService.DelegatorServiceBase
{
    /// <inheritdoc />
    public override async Task<GetDelegatorInfoResponse> GetDelegatorInfo(
        GetDelegatorInfoRequest request, ServerCallContext context)
    {
        var info = await delegatee.GetInfoAsync(context.CancellationToken);
        return new GetDelegatorInfoResponse
        {
            Pubkey = info.Pubkey,
            Fee = info.Fee,
            DelegatorAddress = info.DelegatorAddress
        };
    }

    /// <inheritdoc />
    public override Task<DelegateResponse> Delegate(DelegateRequest request, ServerCallContext context)
        => throw new RpcException(new Status(StatusCode.Unimplemented, "Delegate intake lands in Task 7"));
}
