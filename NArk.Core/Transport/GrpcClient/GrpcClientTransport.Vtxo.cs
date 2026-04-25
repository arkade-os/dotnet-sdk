using System.Runtime.CompilerServices;
using Ark.V1;
using Grpc.Core;
using NArk.Abstractions.VTXOs;
using NBitcoin;

namespace NArk.Transport.GrpcClient;

public partial class GrpcClientTransport
{
    public IAsyncEnumerable<ArkVtxo> GetVtxoByScriptsAsSnapshot(IReadOnlySet<string> scripts,
        DateTimeOffset? after, DateTimeOffset? before,
        CancellationToken cancellationToken = default)
    {
        return GetVtxoByScriptsAsSnapshotCore(scripts,
            after?.ToUnixTimeMilliseconds() ?? 0,
            before?.ToUnixTimeMilliseconds() ?? 0,
            cancellationToken);
    }

    public IAsyncEnumerable<ArkVtxo> GetVtxoByScriptsAsSnapshot(IReadOnlySet<string> scripts,
        CancellationToken cancellationToken = default)
    {
        return GetVtxoByScriptsAsSnapshotCore(scripts, 0, 0, cancellationToken);
    }

    private async IAsyncEnumerable<ArkVtxo> GetVtxoByScriptsAsSnapshotCore(IReadOnlySet<string> scripts,
        long afterMs, long beforeMs,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var scriptsChunk in scripts.Chunk(1000))
        {
            var request = new GetVtxosRequest()
            {
                Scripts = { scriptsChunk },
                RecoverableOnly = false,
                SpendableOnly = false,
                SpentOnly = false,
                Page = new IndexerPageRequest()
                {
                    Index = 0,
                    Size = 1000
                },
                PendingOnly = false,
                After = afterMs,
                Before = beforeMs,
            };

            GetVtxosResponse? response = null;

            // arkd's paginator is 1-based and clamps `next` to `total` on the final page
            // (see arkd internal/core/application/indexer.go paginate()). The correct "more pages"
            // condition is therefore `current < total` — using `next != total` exits one page
            // early (fetches only `total - 1` pages), which is how 11k-VTXO wallets appeared to
            // cap at exactly 11 × page_size instead of all items.
            while (response is null || response.Page.Current < response.Page.Total)
            {
                cancellationToken.ThrowIfCancellationRequested();
                response = await _indexerServiceClient.GetVtxosAsync(request, cancellationToken: cancellationToken);

                foreach (var vtxo in response.Vtxos)
                {
                    DateTimeOffset? expiresAt = null;
                    var maybeExpiresAt = DateTimeOffset.FromUnixTimeSeconds(vtxo.ExpiresAt);
                    if (maybeExpiresAt.Year >= 2025)
                        expiresAt = maybeExpiresAt;

                    uint? expiresAtHeight = expiresAt.HasValue ? null : (uint)vtxo.ExpiresAt;

                    yield return new ArkVtxo(
                        vtxo.Script,
                        vtxo.Outpoint.Txid,
                        vtxo.Outpoint.Vout,
                        vtxo.Amount,
                        vtxo.SpentBy,
                        vtxo.SettledBy,
                        vtxo.IsSwept,
                        DateTimeOffset.FromUnixTimeSeconds(vtxo.CreatedAt),
                        expiresAt,
                        expiresAtHeight,
                        Preconfirmed: vtxo.IsPreconfirmed,
                        Unrolled: vtxo.IsUnrolled,
                        CommitmentTxids: vtxo.CommitmentTxids.ToList(),
                        ArkTxid: string.IsNullOrEmpty(vtxo.ArkTxid) ? null : vtxo.ArkTxid,
                        Assets: vtxo.Assets.Count > 0
                            ? vtxo.Assets.Select(a => new VtxoAsset(a.AssetId, a.Amount)).ToList()
                            : null
                    );
                }

                request.Page.Index = response.Page.Next;
            }
        }
    }

    public async IAsyncEnumerable<HashSet<string>> GetVtxoToPollAsStream(IReadOnlySet<string> scripts,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        var req = new SubscribeForScriptsRequest { SubscriptionId = string.Empty };
        req.Scripts.AddRange(scripts);

        var subscribeRes = await _indexerServiceClient.SubscribeForScriptsAsync(req, cancellationToken: token);

        var stream = _indexerServiceClient.GetSubscription(new GetSubscriptionRequest { SubscriptionId = subscribeRes.SubscriptionId }, cancellationToken: token);

        await foreach (var response in stream.ResponseStream.ReadAllAsync(token))
        {
            if (response == null) continue;
            switch (response.DataCase)
            {
                case GetSubscriptionResponse.DataOneofCase.None:
                case GetSubscriptionResponse.DataOneofCase.Heartbeat:
                    break;
                case GetSubscriptionResponse.DataOneofCase.Event when response.Event is not null:
                    yield return response.Event.Scripts.ToHashSet();
                    break;
                default:
                    throw new InvalidDataException("Operator error: unexpected response from indexer");
            }
        }
    }

    public async IAsyncEnumerable<ArkVtxo> GetVtxosByOutpoints(
        IReadOnlyCollection<OutPoint> outpoints,
        bool spentOnly = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var chunk in outpoints.Chunk(1000))
        {
            var request = new GetVtxosRequest
            {
                Outpoints = { chunk.Select(op => $"{op.Hash}:{op.N}") },
                SpentOnly = spentOnly,
                SpendableOnly = false,
                RecoverableOnly = false,
                PendingOnly = false,
                Page = new IndexerPageRequest { Index = 0, Size = 1000 },
                Before = 0,
                After = 0,
            };

            GetVtxosResponse? response = null;

            // arkd's paginator is 1-based and clamps `next` to `total` on the final page
            // (see arkd internal/core/application/indexer.go paginate()). The correct "more pages"
            // condition is therefore `current < total` — using `next != total` exits one page
            // early (fetches only `total - 1` pages), which is how 11k-VTXO wallets appeared to
            // cap at exactly 11 × page_size instead of all items.
            while (response is null || response.Page.Current < response.Page.Total)
            {
                cancellationToken.ThrowIfCancellationRequested();
                response = await _indexerServiceClient.GetVtxosAsync(request, cancellationToken: cancellationToken);

                foreach (var vtxo in response.Vtxos)
                {
                    DateTimeOffset? expiresAt = null;
                    var maybeExpiresAt = DateTimeOffset.FromUnixTimeSeconds(vtxo.ExpiresAt);
                    if (maybeExpiresAt.Year >= 2025)
                        expiresAt = maybeExpiresAt;

                    uint? expiresAtHeight = expiresAt.HasValue ? null : (uint)vtxo.ExpiresAt;

                    yield return new ArkVtxo(
                        vtxo.Script,
                        vtxo.Outpoint.Txid,
                        vtxo.Outpoint.Vout,
                        vtxo.Amount,
                        vtxo.SpentBy,
                        vtxo.SettledBy,
                        vtxo.IsSwept,
                        DateTimeOffset.FromUnixTimeSeconds(vtxo.CreatedAt),
                        expiresAt,
                        expiresAtHeight,
                        Preconfirmed: vtxo.IsPreconfirmed,
                        Unrolled: vtxo.IsUnrolled,
                        CommitmentTxids: vtxo.CommitmentTxids.ToList(),
                        ArkTxid: string.IsNullOrEmpty(vtxo.ArkTxid) ? null : vtxo.ArkTxid,
                        Assets: vtxo.Assets.Count > 0
                            ? vtxo.Assets.Select(a => new VtxoAsset(a.AssetId, a.Amount)).ToList()
                            : null
                    );
                }

                request.Page.Index = response.Page.Next;
            }
        }
    }

}
