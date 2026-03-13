using System.Net.Http.Json;
using System.Text.Json;
using NArk.Abstractions.Intents;
using NArk.Core;

namespace NArk.Transport.RestClient;

public partial class RestClientTransport
{
    public async Task<string> RegisterIntent(ArkIntent intent, CancellationToken cancellationToken = default)
    {
        try
        {
            var body = new
            {
                intent = new
                {
                    message = intent.RegisterProofMessage,
                    proof = intent.RegisterProof
                }
            };

            var response = await _http.PostAsJsonAsync("/v1/batch/registerIntent", body, JsonOpts, cancellationToken);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOpts, cancellationToken);
            return json.GetProperty("intent_id").GetString()!;
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("duplicated input"))
        {
            throw new AlreadyLockedVtxoException("VTXO is already locked by another intent");
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("already spent") || ex.Message.Contains("VTXO_ALREADY_SPENT"))
        {
            throw new VtxoAlreadySpentException($"VTXO input was already spent in a batch: {ex.Message}");
        }
    }

    public async Task DeleteIntent(ArkIntent intent, CancellationToken cancellationToken = default)
    {
        var body = new
        {
            intent = new
            {
                message = intent.DeleteProofMessage,
                proof = intent.DeleteProof
            }
        };

        var response = await _http.PostAsJsonAsync("/v1/batch/deleteIntent", body, JsonOpts, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
