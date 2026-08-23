using Ark.V1;
using NArk.Abstractions.VirtualTxs;
using NArk.Core.Transport.Models;
using NBitcoin;

namespace NArk.Transport.GrpcClient;

public partial class GrpcClientTransport
{
    public async Task<IReadOnlyList<VtxoChainEntry>> GetVtxoChainAsync(
        OutPoint vtxoOutpoint, string? intentProof = null, string? intentMessage = null,
        CancellationToken cancellationToken = default)
    {
        var result = new List<VtxoChainEntry>();

        if (intentProof is not null && intentMessage is not null)
        {
            var request = new GetVtxoChainRequest
            {
                Intent = new IndexerIntent { Proof = intentProof, Message = intentMessage }
            };

            var authToken = string.Empty;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var response = await _indexerServiceClient.GetVtxoChainAsync(request, cancellationToken: cancellationToken);

                foreach (var entry in response.Chain)
                {
                    result.Add(ToEntry(entry));
                }

                // the first response carries the auth token reused for every later page.
                if (!string.IsNullOrEmpty(response.AuthToken))
                {
                    authToken = response.AuthToken;
                }
                // if there's no next page token, this is the last page 
                if (string.IsNullOrEmpty(response.NextPageToken))
                {
                    break;
                }

                request = new GetVtxoChainRequest
                {
                    Outpoint = new IndexerOutpoint { Txid = vtxoOutpoint.Hash.ToString(), Vout = vtxoOutpoint.N },
                    Token = authToken,
                    PageToken = response.NextPageToken
                };
            }

            return result;
        }

        // Legacy anonymous flow (public tx exposure): outpoint + page-number pagination.
        var legacyRequest = new GetVtxoChainRequest
        {
            Outpoint = new IndexerOutpoint { Txid = vtxoOutpoint.Hash.ToString(), Vout = vtxoOutpoint.N },
            Page = new IndexerPageRequest { Index = 0, Size = 1000 }
        };

        GetVtxoChainResponse? legacyResponse = null;
        while (legacyResponse is null || legacyResponse.Page.Next != legacyResponse.Page.Total)
        {
            cancellationToken.ThrowIfCancellationRequested();
            legacyResponse = await _indexerServiceClient.GetVtxoChainAsync(legacyRequest, cancellationToken: cancellationToken);

            result.AddRange(legacyResponse.Chain.Select(ToEntry));

            legacyRequest.Page.Index = legacyResponse.Page.Next;
        }

        return result;
    }

    private static VtxoChainEntry ToEntry(IndexerChain entry) => new(
        entry.Txid,
        DateTimeOffset.FromUnixTimeSeconds(entry.ExpiresAt),
        MapChainedTxType(entry.Type),
        entry.Spends.ToList());

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
