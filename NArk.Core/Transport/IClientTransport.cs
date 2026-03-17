using NArk.Abstractions.Batches;
using NArk.Abstractions.Batches.ServerEvents;
using NArk.Abstractions.Intents;
using NArk.Abstractions.VTXOs;
using NArk.Core.Transport.Models;
using NBitcoin;

namespace NArk.Core.Transport;

public interface IClientTransport
{
    Task<ArkServerInfo> GetServerInfoAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<HashSet<string>> GetVtxoToPollAsStream(IReadOnlySet<string> scripts, CancellationToken token = default);
    IAsyncEnumerable<ArkVtxo> GetVtxoByScriptsAsSnapshot(IReadOnlySet<string> scripts,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries arkd indexer for VTXOs by outpoints, optionally filtering by spent status.
    /// </summary>
    IAsyncEnumerable<ArkVtxo> GetVtxosByOutpoints(IReadOnlyCollection<OutPoint> outpoints,
        bool spentOnly = false, CancellationToken cancellationToken = default);
    Task<string> RegisterIntent(ArkIntent intent, CancellationToken cancellationToken = default);
    Task DeleteIntent(ArkIntent intent, CancellationToken cancellationToken = default);
    Task<SubmitTxResponse> SubmitTx(string signedArkTx, string[] checkpointTxs, CancellationToken cancellationToken = default);
    Task FinalizeTx(string arkTxId, string[] finalCheckpointTxs, CancellationToken cancellationToken);
    Task SubmitTreeNoncesAsync(SubmitTreeNoncesRequest treeNonces, CancellationToken cancellationToken);
    Task SubmitTreeSignaturesRequest(SubmitTreeSignaturesRequest treeSigs,
        CancellationToken cancellationToken);
    Task SubmitSignedForfeitTxsAsync(SubmitSignedForfeitTxsRequest req, CancellationToken cancellationToken);
    Task ConfirmRegistrationAsync(string intentId, CancellationToken cancellationToken);
    IAsyncEnumerable<BatchEvent> GetEventStreamAsync(GetEventStreamRequest req, CancellationToken cancellationToken);
    Task<ArkAssetDetails> GetAssetDetailsAsync(string assetId, CancellationToken cancellationToken = default);
    Task UpdateStreamTopicsAsync(string streamId, string[]? addTopics, string[]? removeTopics, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the chain of virtual txs from commitment tx to the given VTXO leaf.
    /// </summary>
    Task<IReadOnlyList<VtxoChainEntry>> GetVtxoChainAsync(OutPoint vtxoOutpoint, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns raw tx hex for the given virtual transaction IDs.
    /// </summary>
    Task<IReadOnlyList<string>> GetVirtualTxsAsync(IReadOnlyList<string> txids, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the full VTXO tree structure for a batch outpoint.
    /// </summary>
    Task<IReadOnlyList<VtxoTreeNode>> GetVtxoTreeAsync(OutPoint batchOutpoint, CancellationToken cancellationToken = default);
}