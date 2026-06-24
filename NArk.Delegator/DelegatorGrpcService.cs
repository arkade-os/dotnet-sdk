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
    public override async Task<DelegateResponse> Delegate(DelegateRequest request, ServerCallContext context)
    {
        try
        {
            await delegatee.AcceptAsync(
                request.Intent.Message, request.Intent.Proof,
                request.ForfeitTxs.ToArray(), request.RejectReplace, context.CancellationToken);
            return new DelegateResponse();
        }
        catch (DelegationRejectedException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
    }
}
