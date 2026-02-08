using System.Runtime.CompilerServices;
using Grpc.Core;
using NArk.Abstractions.Batches;
using NArk.Abstractions.Batches.ServerEvents;

namespace NArk.Transport.GrpcClient;

public partial class GrpcClientTransport
{

    public async Task SubmitTreeNoncesAsync(SubmitTreeNoncesRequest treeNonces, CancellationToken cancellationToken)
    {
        var request = new Ark.V1.SubmitTreeNoncesRequest
        {
            BatchId = treeNonces.BatchId,
            Pubkey = treeNonces.PubKey,
            TreeNonces = { treeNonces.Nonces }
        };

        await _serviceClient.SubmitTreeNoncesAsync(
            request,
            cancellationToken: cancellationToken);
    }

    public async Task SubmitTreeSignaturesRequest(SubmitTreeSignaturesRequest treeSigs,
        CancellationToken cancellationToken)
    {
        var request = new Ark.V1.SubmitTreeSignaturesRequest
        {
            BatchId = treeSigs.BatchId,
            Pubkey = treeSigs.PubKey,
            TreeSignatures = { treeSigs.TreeSignatures }
        };

        await _serviceClient.SubmitTreeSignaturesAsync(
            request,
            cancellationToken: cancellationToken);
    }

    public async Task SubmitSignedForfeitTxsAsync(SubmitSignedForfeitTxsRequest req, CancellationToken cancellationToken)
    {
        await _serviceClient.SubmitSignedForfeitTxsAsync(new Ark.V1.SubmitSignedForfeitTxsRequest
        {
            SignedForfeitTxs = { req.SignedForfeitTxs }
        }, cancellationToken: cancellationToken);
    }

    public async Task ConfirmRegistrationAsync(string intentId, CancellationToken cancellationToken)
    {
        await _serviceClient.ConfirmRegistrationAsync(new Ark.V1.ConfirmRegistrationRequest()
        {
            IntentId = intentId
        }, cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<BatchEvent> GetEventStreamAsync(GetEventStreamRequest req, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = _serviceClient.GetEventStream(new Ark.V1.GetEventStreamRequest() { Topics = { req.Topics } }, cancellationToken: cancellationToken);
        await foreach (var e in response.ResponseStream.ReadAllAsync(cancellationToken))
        {
            switch (e.EventCase)
            {
                case Ark.V1.GetEventStreamResponse.EventOneofCase.None:
                    break;
                case Ark.V1.GetEventStreamResponse.EventOneofCase.BatchStarted:
                    yield return new BatchStartedEvent(e.BatchStarted.Id, ParseSequence(e.BatchStarted.BatchExpiry),
                        e.BatchStarted.IntentIdHashes);
                    break;
                case Ark.V1.GetEventStreamResponse.EventOneofCase.BatchFinalization:
                    yield return new BatchFinalizationEvent(e.BatchFinalization.CommitmentTx, e.BatchFinalization.Id);
                    break;
                case Ark.V1.GetEventStreamResponse.EventOneofCase.BatchFinalized:
                    yield return new BatchFinalizedEvent(e.BatchFinalized.CommitmentTxid, e.BatchFinalized.Id);
                    break;
                case Ark.V1.GetEventStreamResponse.EventOneofCase.BatchFailed:
                    yield return new BatchFailedEvent(e.BatchFailed.Id, e.BatchFailed.Reason);
                    break;
                case Ark.V1.GetEventStreamResponse.EventOneofCase.TreeSigningStarted:
                    yield return new TreeSigningStartedEvent(e.TreeSigningStarted.UnsignedCommitmentTx, e.TreeSigningStarted.Id, e.TreeSigningStarted.CosignersPubkeys.ToArray());
                    break;
                case Ark.V1.GetEventStreamResponse.EventOneofCase.TreeNoncesAggregated:
                    yield return new TreeNoncesAggregatedEvent(e.TreeNoncesAggregated.Id, e.TreeNoncesAggregated.TreeNonces.ToDictionary());
                    break;
                case Ark.V1.GetEventStreamResponse.EventOneofCase.TreeTx:
                    yield return new TreeTxEvent(e.TreeTx.Id, e.TreeTx.BatchIndex, e.TreeTx.Children.ToDictionary(),
                        e.TreeTx.Topic, e.TreeTx.Tx, e.TreeTx.Txid);
                    break;
                case Ark.V1.GetEventStreamResponse.EventOneofCase.TreeSignature:
                    yield return new TreeSignatureEvent(e.TreeSignature.BatchIndex, e.TreeSignature.Id,
                        e.TreeSignature.Signature, e.TreeSignature.Topic, e.TreeSignature.Txid);
                    break;
                case Ark.V1.GetEventStreamResponse.EventOneofCase.TreeNonces:
                    yield return new TreeNoncesEvent(e.TreeNonces.Id, e.TreeNonces.Nonces.ToDictionary(), e.TreeNonces.Topic, e.TreeNonces.Txid);
                    break;
                case Ark.V1.GetEventStreamResponse.EventOneofCase.Heartbeat:
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }
}