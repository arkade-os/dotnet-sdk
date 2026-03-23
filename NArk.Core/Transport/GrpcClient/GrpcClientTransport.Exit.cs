using Ark.V1;
using NArk.Core.Transport.Models;
using NBitcoin;

namespace NArk.Transport.GrpcClient;

public partial class GrpcClientTransport
{
    public async Task<IReadOnlyList<VtxoChainEntry>> GetVtxoChainAsync(
        OutPoint vtxoOutpoint, CancellationToken cancellationToken = default)
    {
        var result = new List<VtxoChainEntry>();
        var request = new GetVtxoChainRequest
        {
            Outpoint = new IndexerOutpoint { Txid = vtxoOutpoint.Hash.ToString(), Vout = vtxoOutpoint.N },
            Page = new IndexerPageRequest { Index = 0, Size = 1000 }
        };

        GetVtxoChainResponse? response = null;
        while (response is null || response.Page.Next != response.Page.Total)
        {
            cancellationToken.ThrowIfCancellationRequested();
            response = await _indexerServiceClient.GetVtxoChainAsync(request, cancellationToken: cancellationToken);

            foreach (var entry in response.Chain)
            {
                result.Add(new VtxoChainEntry(
                    entry.Txid,
                    DateTimeOffset.FromUnixTimeSeconds(entry.ExpiresAt),
                    MapChainedTxType(entry.Type),
                    entry.Spends.ToList()
                ));
            }

            request.Page.Index = response.Page.Next;
        }

        return result;
    }

    public async Task<IReadOnlyList<string>> GetVirtualTxsAsync(
        IReadOnlyList<string> txids, CancellationToken cancellationToken = default)
    {
        var result = new List<string>();

        foreach (var chunk in txids.Chunk(100))
        {
            var request = new GetVirtualTxsRequest { Page = new IndexerPageRequest { Index = 0, Size = 1000 } };
            request.Txids.AddRange(chunk);

            GetVirtualTxsResponse? response = null;
            while (response is null || response.Page.Next != response.Page.Total)
            {
                cancellationToken.ThrowIfCancellationRequested();
                response = await _indexerServiceClient.GetVirtualTxsAsync(request, cancellationToken: cancellationToken);
                result.AddRange(response.Txs);
                request.Page.Index = response.Page.Next;
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<VtxoTreeNode>> GetVtxoTreeAsync(
        OutPoint batchOutpoint, CancellationToken cancellationToken = default)
    {
        var result = new List<VtxoTreeNode>();
        var request = new GetVtxoTreeRequest
        {
            BatchOutpoint = new IndexerOutpoint { Txid = batchOutpoint.Hash.ToString(), Vout = batchOutpoint.N },
            Page = new IndexerPageRequest { Index = 0, Size = 1000 }
        };

        GetVtxoTreeResponse? response = null;
        while (response is null || response.Page.Next != response.Page.Total)
        {
            cancellationToken.ThrowIfCancellationRequested();
            response = await _indexerServiceClient.GetVtxoTreeAsync(request, cancellationToken: cancellationToken);

            foreach (var node in response.VtxoTree)
            {
                result.Add(new VtxoTreeNode(
                    node.Txid,
                    node.Children.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
                ));
            }

            request.Page.Index = response.Page.Next;
        }

        return result;
    }

    private static ChainedTxType MapChainedTxType(IndexerChainedTxType type) => type switch
    {
        IndexerChainedTxType.Commitment => ChainedTxType.Commitment,
        IndexerChainedTxType.Ark => ChainedTxType.Ark,
        IndexerChainedTxType.Tree => ChainedTxType.Tree,
        IndexerChainedTxType.Checkpoint => ChainedTxType.Checkpoint,
        _ => ChainedTxType.Unspecified
    };
}
