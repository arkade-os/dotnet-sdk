using System.Net.Http.Json;
using System.Text.Json;
using NArk.Core.Extensions;
using NArk.Core.Transport.Models;

namespace NArk.Transport.RestClient;

public partial class RestClientTransport
{
    public async Task<SubmitTxResponse> SubmitTx(string signedArkTx, string[] checkpointTxs,
        CancellationToken cancellationToken = default)
    {
        var body = new
        {
            signed_ark_tx = signedArkTx,
            checkpoint_txs = checkpointTxs
        };

        var response = await _http.PostAsJsonAsync("/v1/tx/submit", body, JsonOpts, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOpts, cancellationToken);

        return new SubmitTxResponse(
            json.GetPropInvariantCase("ark_txid").GetString()!,
            json.GetPropInvariantCase("final_ark_tx").GetString()!,
            json.TryGetPropInvariantCase("signed_checkpoint_txs", out var scts) && scts.ValueKind == JsonValueKind.Array
                ? scts.EnumerateArray().Select(e => e.GetString()!).ToArray()
                : Array.Empty<string>()
        );
    }

    public async Task FinalizeTx(string arkTxId, string[] finalCheckpointTxs,
        CancellationToken cancellationToken)
    {
        var body = new
        {
            ark_txid = arkTxId,
            final_checkpoint_txs = finalCheckpointTxs
        };

        var response = await _http.PostAsJsonAsync("/v1/tx/finalize", body, JsonOpts, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
