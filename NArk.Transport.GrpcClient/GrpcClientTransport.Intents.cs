using Ark.V1;
using NArk.Abstractions.Intents;

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
}