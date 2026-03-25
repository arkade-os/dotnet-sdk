using Ark.V1;
using NArk.Abstractions.Intents;
using NArk.Core;

namespace NArk.Transport.GrpcClient;

public partial class GrpcClientTransport
{
    public async Task<string> RegisterIntent(ArkIntent intent, CancellationToken cancellationToken = default)
    {
        try
        {
            var registerRequest = new RegisterIntentRequest
            {
                Intent = new Intent()
                {
                    Message = intent.RegisterProofMessage,
                    Proof = intent.RegisterProof
                }
            };

            var response =
                await _serviceClient.RegisterIntentAsync(registerRequest, cancellationToken: cancellationToken);

            return response.IntentId;
        }
        catch (OperationCanceledException)
        {
            // ignored
            return string.Empty;
        }
        catch (Exception ex) when (ex.Message.Contains("duplicated input"))
        {
            throw new AlreadyLockedVtxoException("VTXO is already locked by another intent");
        }
        catch (Exception ex) when (ex.Message.Contains("already spent") || ex.Message.Contains("VTXO_ALREADY_SPENT"))
        {
            throw new VtxoAlreadySpentException($"VTXO input was already spent in a batch: {ex.Message}");
        }
    }

    public async Task DeleteIntent(ArkIntent intent, CancellationToken cancellationToken = default)
    {
        var deleteRequest = new DeleteIntentRequest
        {
            Intent = new Intent()
            {
                Message = intent.DeleteProofMessage,
                Proof = intent.DeleteProof
            }
        };

        await _serviceClient.DeleteIntentAsync(deleteRequest, cancellationToken: cancellationToken);
    }

    public async Task<ArkIntent[]> GetIntentsByProofAsync(string proof, string message,
        CancellationToken cancellationToken = default)
    {
        var request = new GetIntentRequest
        {
            Intent = new Intent
            {
                Proof = proof,
                Message = message
            }
        };

        var response = await _serviceClient.GetIntentAsync(request, cancellationToken: cancellationToken);

        return response.Intents.Select(i => new ArkIntent(
            IntentTxId: string.Empty,
            IntentId: null,
            WalletId: string.Empty,
            State: ArkIntentState.WaitingToSubmit,
            ValidFrom: null,
            ValidUntil: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            RegisterProof: i.Proof,
            RegisterProofMessage: i.Message,
            DeleteProof: string.Empty,
            DeleteProofMessage: string.Empty,
            BatchId: null,
            CommitmentTransactionId: null,
            CancellationReason: null,
            IntentVtxos: [],
            SignerDescriptor: string.Empty)).ToArray();
    }
}