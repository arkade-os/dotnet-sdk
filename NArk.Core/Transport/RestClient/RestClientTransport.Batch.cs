using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using NArk.Abstractions.Batches;
using NArk.Abstractions.Batches.ServerEvents;
using NBitcoin;

namespace NArk.Transport.RestClient;

public partial class RestClientTransport
{
    public async Task SubmitTreeNoncesAsync(SubmitTreeNoncesRequest treeNonces, CancellationToken cancellationToken)
    {
        var body = new
        {
            batch_id = treeNonces.BatchId,
            pubkey = treeNonces.PubKey,
            tree_nonces = treeNonces.Nonces
        };

        var response = await _http.PostAsJsonAsync("/v1/batch/tree/submitNonces", body, JsonOpts, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task SubmitTreeSignaturesRequest(SubmitTreeSignaturesRequest treeSigs, CancellationToken cancellationToken)
    {
        var body = new
        {
            batch_id = treeSigs.BatchId,
            pubkey = treeSigs.PubKey,
            tree_signatures = treeSigs.TreeSignatures
        };

        var response = await _http.PostAsJsonAsync("/v1/batch/tree/submitSignatures", body, JsonOpts, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task SubmitSignedForfeitTxsAsync(SubmitSignedForfeitTxsRequest req, CancellationToken cancellationToken)
    {
        var body = new
        {
            signed_forfeit_txs = req.SignedForfeitTxs,
            signed_commitment_tx = req.SignedCommitmentTx
        };

        var response = await _http.PostAsJsonAsync("/v1/batch/submitForfeitTxs", body, JsonOpts, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task ConfirmRegistrationAsync(string intentId, CancellationToken cancellationToken)
    {
        var body = new { intent_id = intentId };
        var response = await _http.PostAsJsonAsync("/v1/batch/ack", body, JsonOpts, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task UpdateStreamTopicsAsync(string streamId, string[]? addTopics, string[]? removeTopics,
        CancellationToken cancellationToken = default)
    {
        var body = new
        {
            stream_id = streamId,
            modify = new
            {
                add_topics = addTopics ?? Array.Empty<string>(),
                remove_topics = removeTopics ?? Array.Empty<string>()
            }
        };

        var response = await _http.PostAsJsonAsync("/v1/batch/updateTopics", body, JsonOpts, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Streams batch events from arkd's SSE endpoint.
    /// gRPC-gateway exposes server-streaming RPCs as newline-delimited JSON over HTTP.
    /// </summary>
    public async IAsyncEnumerable<BatchEvent> GetEventStreamAsync(
        GetEventStreamRequest req,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Build query string with topics
        var queryParts = req.Topics.Select(t => $"topics={Uri.EscapeDataString(t)}");
        var queryString = string.Join("&", queryParts);
        var url = $"/v1/batch/events?{queryString}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonElement json;
            try { json = JsonSerializer.Deserialize<JsonElement>(line, JsonOpts); }
            catch { continue; }

            var evt = ParseBatchEvent(json);
            if (evt is not null)
                yield return evt;
        }
    }

    private BatchEvent? ParseBatchEvent(JsonElement json)
    {
        // gRPC-gateway wraps the oneof in a "result" envelope for server streaming
        var root = json.TryGetProperty("result", out var result) ? result : json;

        if (root.TryGetProperty("heartbeat", out _))
            return null;

        if (root.TryGetProperty("stream_started", out var ss))
            return new StreamStartedEvent(ss.GetProperty("id").GetString()!);

        if (root.TryGetProperty("batch_started", out var bs))
        {
            var intentHashes = new List<string>();
            if (bs.TryGetProperty("intent_id_hashes", out var ih))
                foreach (var h in ih.EnumerateArray())
                    if (h.GetString() is { } s) intentHashes.Add(s);

            return new BatchStartedEvent(
                bs.GetProperty("id").GetString()!,
                ParseSequence(bs.GetProperty("batch_expiry").GetInt64()),
                intentHashes);
        }

        if (root.TryGetProperty("batch_finalization", out var bf))
            return new BatchFinalizationEvent(
                bf.GetProperty("commitment_tx").GetString()!,
                bf.GetProperty("id").GetString()!);

        if (root.TryGetProperty("batch_finalized", out var bfd))
            return new BatchFinalizedEvent(
                bfd.GetProperty("commitment_txid").GetString()!,
                bfd.GetProperty("id").GetString()!);

        if (root.TryGetProperty("batch_failed", out var bfl))
            return new BatchFailedEvent(
                bfl.GetProperty("id").GetString()!,
                bfl.GetProperty("reason").GetString()!);

        if (root.TryGetProperty("tree_signing_started", out var tss))
        {
            var cosigners = Array.Empty<string>();
            if (tss.TryGetProperty("cosigners_pubkeys", out var cp))
                cosigners = cp.EnumerateArray().Select(e => e.GetString()!).ToArray();

            return new TreeSigningStartedEvent(
                tss.GetProperty("unsigned_commitment_tx").GetString()!,
                tss.GetProperty("id").GetString()!,
                cosigners);
        }

        if (root.TryGetProperty("tree_nonces_aggregated", out var tna))
        {
            var nonces = new Dictionary<string, string>();
            if (tna.TryGetProperty("tree_nonces", out var tn) && tn.ValueKind == JsonValueKind.Object)
                foreach (var prop in tn.EnumerateObject())
                    nonces[prop.Name] = prop.Value.GetString()!;

            return new TreeNoncesAggregatedEvent(
                tna.GetProperty("id").GetString()!,
                nonces);
        }

        if (root.TryGetProperty("tree_tx", out var ttx))
        {
            var children = new Dictionary<uint, string>();
            if (ttx.TryGetProperty("children", out var ch) && ch.ValueKind == JsonValueKind.Object)
                foreach (var prop in ch.EnumerateObject())
                    if (uint.TryParse(prop.Name, out var key))
                        children[key] = prop.Value.GetString()!;

            var topics = new List<string>();
            if (ttx.TryGetProperty("topic", out var tp) && tp.ValueKind == JsonValueKind.Array)
                foreach (var t in tp.EnumerateArray())
                    if (t.GetString() is { } s) topics.Add(s);

            return new TreeTxEvent(
                ttx.GetProperty("id").GetString()!,
                ttx.GetProperty("batch_index").GetInt32(),
                children, topics,
                ttx.GetProperty("tx").GetString()!,
                ttx.GetProperty("txid").GetString()!);
        }

        if (root.TryGetProperty("tree_signature", out var tsig))
        {
            var topics = new List<string>();
            if (tsig.TryGetProperty("topic", out var tp) && tp.ValueKind == JsonValueKind.Array)
                foreach (var t in tp.EnumerateArray())
                    if (t.GetString() is { } s) topics.Add(s);

            return new TreeSignatureEvent(
                tsig.GetProperty("batch_index").GetInt32(),
                tsig.GetProperty("id").GetString()!,
                tsig.GetProperty("signature").GetString()!,
                topics,
                tsig.GetProperty("txid").GetString()!);
        }

        if (root.TryGetProperty("tree_nonces", out var tnonces))
        {
            var nonces = new Dictionary<string, string>();
            if (tnonces.TryGetProperty("nonces", out var n) && n.ValueKind == JsonValueKind.Object)
                foreach (var prop in n.EnumerateObject())
                    nonces[prop.Name] = prop.Value.GetString()!;

            var topics = new List<string>();
            if (tnonces.TryGetProperty("topic", out var tp) && tp.ValueKind == JsonValueKind.Array)
                foreach (var t in tp.EnumerateArray())
                    if (t.GetString() is { } s) topics.Add(s);

            return new TreeNoncesEvent(
                tnonces.GetProperty("id").GetString()!,
                nonces, topics,
                tnonces.GetProperty("txid").GetString()!);
        }

        return null; // Unknown event type
    }
}
