using Ark.V1;
using SubmitTxResponse = NArk.Core.Transport.Models.SubmitTxResponse;

namespace NArk.Transport.GrpcClient;

public partial class GrpcClientTransport
{
    public async Task<SubmitTxResponse> SubmitTx(string signedArkTx, string[] checkpointTxs, CancellationToken cancellationToken = default)
    {
        var submitRequest = new SubmitTxRequest
        {
            SignedArkTx = signedArkTx,
            CheckpointTxs = { checkpointTxs }
        };

        var response = await _serviceClient.SubmitTxAsync(submitRequest, cancellationToken: cancellationToken);

        return new SubmitTxResponse(
            response.ArkTxid,
            response.FinalArkTx,
            response.SignedCheckpointTxs.ToArray()
        );
    }

    public async Task FinalizeTx(string arkTxId, string[] finalCheckpointTxs, CancellationToken cancellationToken)
    {
        var finalizeRequest = new FinalizeTxRequest()
        {
            ArkTxid = arkTxId,
            FinalCheckpointTxs = { finalCheckpointTxs }
        };

        await _serviceClient.FinalizeTxAsync(finalizeRequest, cancellationToken: cancellationToken);
    }
}