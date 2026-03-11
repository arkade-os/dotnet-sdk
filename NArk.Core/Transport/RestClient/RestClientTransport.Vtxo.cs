using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Web;
using NArk.Abstractions.VTXOs;

namespace NArk.Transport.RestClient;

public partial class RestClientTransport
{
    public async IAsyncEnumerable<ArkVtxo> GetVtxoByScriptsAsSnapshot(
        IReadOnlySet<string> scripts,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var scriptsChunk in scripts.Chunk(1000))
        {
            var pageIndex = 0;
            int? pageTotal = null;

            while (pageTotal is null || pageIndex < pageTotal)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var query = HttpUtility.ParseQueryString(string.Empty);
                foreach (var s in scriptsChunk)
                    query.Add("scripts", s);
                query["page.size"] = "1000";
                query["page.index"] = pageIndex.ToString();

                var json = await _http.GetFromJsonAsync<JsonElement>(
                    $"/v1/indexer/vtxos?{query}", JsonOpts, cancellationToken);

                if (json.TryGetProperty("page", out var page))
                {
                    pageIndex = page.GetProperty("next").GetInt32();
                    pageTotal = page.GetProperty("total").GetInt32();
                }
                else
                {
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

    public async IAsyncEnumerable<HashSet<string>> GetVtxoToPollAsStream(
        IReadOnlySet<string> scripts,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        // Step 1: Subscribe for scripts
        var subReq = new { scripts = scripts.ToArray(), subscription_id = "" };
        var subResponse = await _http.PostAsJsonAsync("/v1/indexer/script/subscribe", subReq, JsonOpts, token);
        subResponse.EnsureSuccessStatusCode();
        var subJson = await subResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOpts, token);
        var subscriptionId = subJson.GetProperty("subscription_id").GetString()!;

        // Step 2: Stream subscription events via SSE (gRPC-gateway server streaming → newline-delimited JSON)
        using var stream = await _http.GetStreamAsync(
            $"/v1/indexer/script/subscription/{subscriptionId}", token);
        using var reader = new StreamReader(stream);

        while (!token.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(token);
            if (line is null) break; // Stream closed
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonElement evt;
            try { evt = JsonSerializer.Deserialize<JsonElement>(line, JsonOpts); }
            catch { continue; }

            // Heartbeat — skip
            if (evt.TryGetProperty("heartbeat", out _)) continue;

            // Subscription event — extract scripts
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
                    yield return changedScripts;
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
