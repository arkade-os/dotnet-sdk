using System.Runtime.CompilerServices;
using Ark.V1;
using Grpc.Core;
using NArk.Abstractions.VTXOs;

namespace NArk.Transport.GrpcClient;

public partial class GrpcClientTransport
{
    public async IAsyncEnumerable<ArkVtxo> GetVtxoByScriptsAsSnapshot(IReadOnlySet<string> scripts, [EnumeratorCancellation] CancellationToken cancellationToken = default)
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
                }
            };

            GetVtxosResponse? response = null;

            while (response is null || response.Page.Next != response.Page.Total)
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
                        ArkTxid: string.IsNullOrEmpty(vtxo.ArkTxid) ? null : vtxo.ArkTxid
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

}