using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NArk.Core.Transport.Models;
using NBitcoin;

namespace NArk.Transport.RestClient;

public partial class RestClientTransport
{
    public async Task<IReadOnlyList<VtxoChainEntry>> GetVtxoChainAsync(
        OutPoint vtxoOutpoint, CancellationToken cancellationToken = default)
    {
        var result = new List<VtxoChainEntry>();
        var pageIndex = 0;
        int? pageTotal = null;

        while (pageTotal is null || pageIndex < pageTotal)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = $"/v1/indexer/vtxo/{vtxoOutpoint.Hash}/{vtxoOutpoint.N}/chain?page.index={pageIndex}&page.size=1000";
            var response = await _http.GetFromJsonAsync<VtxoChainResponse>(url, JsonOpts, cancellationToken);
            if (response is null) break;

            foreach (var entry in response.Chain ?? [])
            {
                result.Add(new VtxoChainEntry(
                    entry.Txid,
                    DateTimeOffset.FromUnixTimeSeconds(long.Parse(entry.ExpiresAt)),
                    Enum.TryParse<ChainedTxType>(entry.Type, true, out var t) ? t : ChainedTxType.Unspecified,
                    entry.Spends ?? []
                ));
            }

            pageTotal = response.Page?.Total ?? 0;
            pageIndex = response.Page?.Next ?? pageTotal.Value;
        }

        return result;
    }

    public async Task<IReadOnlyList<string>> GetVirtualTxsAsync(
        IReadOnlyList<string> txids, CancellationToken cancellationToken = default)
    {
        var result = new List<string>();

        foreach (var chunk in txids.Chunk(100))
        {
            var txidParam = string.Join(",", chunk);
            var pageIndex = 0;
            int? pageTotal = null;

            while (pageTotal is null || pageIndex < pageTotal)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var url = $"/v1/indexer/virtualTx/{txidParam}?page.index={pageIndex}&page.size=1000";
                var response = await _http.GetFromJsonAsync<VirtualTxsResponse>(url, JsonOpts, cancellationToken);
                if (response is null) break;

                result.AddRange(response.Txs ?? []);

                pageTotal = response.Page?.Total ?? 0;
                pageIndex = response.Page?.Next ?? pageTotal.Value;
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<VtxoTreeNode>> GetVtxoTreeAsync(
        OutPoint batchOutpoint, CancellationToken cancellationToken = default)
    {
        var result = new List<VtxoTreeNode>();
        var pageIndex = 0;
        int? pageTotal = null;

        while (pageTotal is null || pageIndex < pageTotal)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = $"/v1/indexer/batch/{batchOutpoint.Hash}/{batchOutpoint.N}/tree?page.index={pageIndex}&page.size=1000";
            var response = await _http.GetFromJsonAsync<VtxoTreeResponse>(url, JsonOpts, cancellationToken);
            if (response is null) break;

            foreach (var node in response.VtxoTree ?? [])
            {
                result.Add(new VtxoTreeNode(
                    node.Txid,
                    node.Children ?? new Dictionary<uint, string>()
                ));
            }

            pageTotal = response.Page?.Total ?? 0;
            pageIndex = response.Page?.Next ?? pageTotal.Value;
        }

        return result;
    }

    // JSON DTOs for deserialization
    private record VtxoChainResponse(
        [property: JsonPropertyName("chain")] List<VtxoChainEntryDto>? Chain,
        [property: JsonPropertyName("page")] PageDto? Page);

    private record VtxoChainEntryDto(
        [property: JsonPropertyName("txid")] string Txid,
        [property: JsonPropertyName("expires_at")] string ExpiresAt,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("spends")] List<string>? Spends);

    private record VirtualTxsResponse(
        [property: JsonPropertyName("txs")] List<string>? Txs,
        [property: JsonPropertyName("page")] PageDto? Page);

    private record VtxoTreeResponse(
        [property: JsonPropertyName("vtxo_tree")] List<VtxoTreeNodeDto>? VtxoTree,
        [property: JsonPropertyName("page")] PageDto? Page);

    private record VtxoTreeNodeDto(
        [property: JsonPropertyName("txid")] string Txid,
        [property: JsonPropertyName("children")] Dictionary<uint, string>? Children);

    private record PageDto(
        [property: JsonPropertyName("current")] int Current,
        [property: JsonPropertyName("next")] int Next,
        [property: JsonPropertyName("total")] int Total);
}
