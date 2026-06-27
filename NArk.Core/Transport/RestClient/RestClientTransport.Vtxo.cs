using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Web;
using NArk.Abstractions.VTXOs;
using NArk.Core.Transport;
using NBitcoin;

namespace NArk.Transport.RestClient;

public partial class RestClientTransport
{
    public IAsyncEnumerable<ArkVtxo> GetVtxoByScriptsAsSnapshot(
        IReadOnlySet<string> scripts,
        DateTimeOffset? after, DateTimeOffset? before,
        CancellationToken cancellationToken = default)
    {
        return GetVtxoByScriptsAsSnapshotCore(scripts,
            after?.ToUnixTimeMilliseconds() ?? 0,
            before?.ToUnixTimeMilliseconds() ?? 0,
            cancellationToken);
    }

    public IAsyncEnumerable<ArkVtxo> GetVtxoByScriptsAsSnapshot(
        IReadOnlySet<string> scripts,
        CancellationToken cancellationToken = default)
    {
        return GetVtxoByScriptsAsSnapshotCore(scripts, 0, 0, cancellationToken);
    }

    private async IAsyncEnumerable<ArkVtxo> GetVtxoByScriptsAsSnapshotCore(
        IReadOnlySet<string> scripts,
        long afterMs, long beforeMs,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var scriptsChunk in scripts.Chunk(1000))
        {
            // arkd's paginator is 1-based and clamps `next` to `total` on the final page.
            // Drive the loop with `current < total` (set from the response) and use `next`
            // only to advance `page.index` for the next request; otherwise we exit one
            // page early and silently lose the last page's VTXOs.
            var pageIndex = 0;
            int? pageCurrent = null;
            int? pageTotal = null;

            while (pageCurrent is null || pageCurrent < pageTotal)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var query = HttpUtility.ParseQueryString(string.Empty);
                foreach (var s in scriptsChunk)
                    query.Add("scripts", s);
                query["page.size"] = "1000";
                query["page.index"] = pageIndex.ToString();
                if (afterMs > 0) query["after"] = afterMs.ToString();
                if (beforeMs > 0) query["before"] = beforeMs.ToString();

                var json = await _http.GetFromJsonAsync<JsonElement>(
                    $"/v1/indexer/vtxos?{query}", JsonOpts, cancellationToken);

                if (json.TryGetProperty("page", out var page))
                {
                    pageCurrent = page.GetProperty("current").GetInt32();
                    pageTotal = page.GetProperty("total").GetInt32();
                    pageIndex = page.GetProperty("next").GetInt32();
                }
                else
                {
                    pageCurrent = 0;
                    pageTotal = 0; // No more pages
                }

                if (!json.TryGetProperty("vtxos", out var vtxosArr)) yield break;

                foreach (var v in vtxosArr.EnumerateArray())
                {
                    yield return ParseVtxo(v);
                }
            }
        }
    }

    public async IAsyncEnumerable<VtxoSubscriptionEvent> OpenSubscriptionStreamAsync(
        IReadOnlySet<string>? initialScripts,
        string? existingSubscriptionId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string url;
        if (existingSubscriptionId is not null)
        {
            url = $"/v1/indexer/script/subscription/{existingSubscriptionId}";
        }
        else
        {
            var query = HttpUtility.ParseQueryString(string.Empty);
            if (initialScripts?.Count > 0)
                foreach (var s in initialScripts)
                    query.Add("filter.scripts.add", s);
            url = query.Count > 0 ? $"/v1/indexer/subscription?{query}" : "/v1/indexer/subscription";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonElement evt;
            try { evt = JsonSerializer.Deserialize<JsonElement>(line, JsonOpts); }
            catch { continue; }

            if (evt.TryGetProperty("heartbeat", out _)) continue;

            if (evt.TryGetProperty("subscriptionStarted", out var started) &&
                started.TryGetProperty("subscriptionId", out var idProp))
            {
                var id = idProp.GetString();
                if (id is not null) yield return new VtxoSubscriptionStarted(id);
                continue;
            }

            if (evt.TryGetProperty("event", out var eventData) &&
                eventData.TryGetProperty("scripts", out var scriptsArr))
            {
                var changedScripts = new HashSet<string>();
                foreach (var s in scriptsArr.EnumerateArray())
                {
                    var val = s.GetString();
                    if (val is not null) changedScripts.Add(val);
                }
                if (changedScripts.Count > 0)
                    yield return new VtxoScriptsChanged(changedScripts);
            }
        }
    }

    public async Task UpdateSubscriptionScriptsAsync(
        string subscriptionId,
        IReadOnlySet<string>? add,
        IReadOnlySet<string>? remove,
        CancellationToken cancellationToken = default)
    {
        if ((add is null || add.Count == 0) && (remove is null || remove.Count == 0))
            return;

        var req = new
        {
            subscription_id = subscriptionId,
            filter = new
            {
                scripts = new
                {
                    add = add?.ToArray() ?? [],
                    remove = remove?.ToArray() ?? [],
                }
            }
        };
        var response = await _http.PostAsJsonAsync("/v1/indexer/subscription/update", req, JsonOpts, cancellationToken);
        await EnsureSuccessWithBodyAsync(response, "UpdateSubscription", cancellationToken);
    }

    // Surfaces the server error body in the exception message so callers can detect arkd's
    // "subscription <id> not found" (the trigger for recreating a GC'd subscription).
    private static async Task EnsureSuccessWithBodyAsync(HttpResponseMessage response, string op, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"{op} failed ({(int)response.StatusCode}): {body}");
    }

    public async IAsyncEnumerable<ArkVtxo> GetVtxosByOutpoints(
        IReadOnlyCollection<OutPoint> outpoints,
        bool spentOnly = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var chunk in outpoints.Chunk(1000))
        {
            // See GetVtxoByScriptsAsSnapshotCore for the pagination rationale.
            var pageIndex = 0;
            int? pageCurrent = null;
            int? pageTotal = null;

            while (pageCurrent is null || pageCurrent < pageTotal)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var query = HttpUtility.ParseQueryString(string.Empty);
                foreach (var op in chunk)
                    query.Add("outpoints", $"{op.Hash}:{op.N}");
                if (spentOnly)
                    query["spent_only"] = "true";
                query["page.size"] = "1000";
                query["page.index"] = pageIndex.ToString();

                var json = await _http.GetFromJsonAsync<JsonElement>(
                    $"/v1/indexer/vtxos?{query}", JsonOpts, cancellationToken);

                if (json.TryGetProperty("page", out var page))
                {
                    pageCurrent = page.GetProperty("current").GetInt32();
                    pageTotal = page.GetProperty("total").GetInt32();
                    pageIndex = page.GetProperty("next").GetInt32();
                }
                else
                {
                    pageCurrent = 0;
                    pageTotal = 0;
                }

                if (!json.TryGetProperty("vtxos", out var vtxosArr)) yield break;

                foreach (var v in vtxosArr.EnumerateArray())
                {
                    yield return ParseVtxo(v);
                }
            }
        }
    }

    private static ArkVtxo ParseVtxo(JsonElement v)
    {
        var outpoint = v.GetProperty("outpoint");
        var txid = outpoint.GetProperty("txid").GetString()!;
        var vout = (uint)outpoint.GetProperty("vout").GetInt32();
        var amount = ulong.Parse(v.GetProperty("amount").GetString() ?? "0");
        var script = v.GetProperty("script").GetString()!;

        var spentBy = v.TryGetProperty("spent_by", out var sb) ? sb.GetString() : null;
        var settledBy = v.TryGetProperty("settled_by", out var stb) ? stb.GetString() : null;
        var isSwept = v.TryGetProperty("is_swept", out var sw) && sw.GetBoolean();
        var isPreconfirmed = v.TryGetProperty("is_preconfirmed", out var pc) && pc.GetBoolean();
        var isUnrolled = v.TryGetProperty("is_unrolled", out var ur) && ur.GetBoolean();

        var createdAt = v.TryGetProperty("created_at", out var ca) && long.TryParse(ca.GetString() ?? ca.ToString(), out var caVal)
            ? DateTimeOffset.FromUnixTimeSeconds(caVal)
            : DateTimeOffset.UtcNow;

        DateTimeOffset? expiresAt = null;
        uint? expiresAtHeight = null;
        if (v.TryGetProperty("expires_at", out var ea) && long.TryParse(ea.GetString() ?? ea.ToString(), out var eaVal))
        {
            var maybeExpires = DateTimeOffset.FromUnixTimeSeconds(eaVal);
            if (maybeExpires.Year >= 2025)
                expiresAt = maybeExpires;
            else
                expiresAtHeight = (uint)eaVal;
        }

        var commitmentTxids = new List<string>();
        if (v.TryGetProperty("commitment_txids", out var ctArr) && ctArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var ct in ctArr.EnumerateArray())
            {
                var val = ct.GetString();
                if (val is not null) commitmentTxids.Add(val);
            }
        }

        var arkTxid = v.TryGetProperty("ark_txid", out var at) ? at.GetString() : null;
        if (string.IsNullOrEmpty(arkTxid)) arkTxid = null;

        List<VtxoAsset>? assets = null;
        if (v.TryGetProperty("assets", out var assetsArr) && assetsArr.ValueKind == JsonValueKind.Array && assetsArr.GetArrayLength() > 0)
        {
            assets = new List<VtxoAsset>();
            foreach (var a in assetsArr.EnumerateArray())
            {
                assets.Add(new VtxoAsset(
                    a.GetProperty("asset_id").GetString()!,
                    ulong.Parse(a.GetProperty("amount").GetString() ?? "0")));
            }
        }

        return new ArkVtxo(
            script, txid, vout, amount, spentBy, settledBy, isSwept,
            createdAt, expiresAt, expiresAtHeight,
            Preconfirmed: isPreconfirmed,
            Unrolled: isUnrolled,
            CommitmentTxids: commitmentTxids,
            ArkTxid: arkTxid,
            Assets: assets);
    }
}
