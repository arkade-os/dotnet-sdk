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

    /// <summary>
    /// Opens a server-streaming subscription for VTXO script events.
    /// <para>
    /// When <paramref name="existingSubscriptionId"/> is <c>null</c>, the server creates a new
    /// subscription. The first yielded event is always <see cref="VtxoSubscriptionStarted"/>
    /// carrying the server-assigned ID. Pass <paramref name="initialScripts"/> to register the
    /// initial watched set at stream-open time (only honoured on new subscriptions).
    /// </para>
    /// <para>
    /// When <paramref name="existingSubscriptionId"/> is provided, the server reconnects to an
    /// existing subscription. If the subscription was GC'd the stream throws a <c>NotFound</c>
    /// error; the caller should retry with a <c>null</c> ID to open a fresh subscription.
    /// </para>
    /// </summary>
    IAsyncEnumerable<VtxoSubscriptionEvent> OpenSubscriptionStreamAsync(
        IReadOnlySet<string>? initialScripts,
        string? existingSubscriptionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the script set of an existing subscription in place without tearing the stream down.
    /// Both <paramref name="add"/> and <paramref name="remove"/> are optional; when both are null
    /// or empty the call is a no-op. Throws <c>NotFound</c> when the subscription was GC'd.
    /// </summary>
    Task UpdateSubscriptionScriptsAsync(
        string subscriptionId,
        IReadOnlySet<string>? add,
        IReadOnlySet<string>? remove,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<ArkVtxo> GetVtxoByScriptsAsSnapshot(IReadOnlySet<string> scripts,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries arkd indexer for VTXOs by scripts, filtered to those updated within the given time range.
    /// </summary>
    IAsyncEnumerable<ArkVtxo> GetVtxoByScriptsAsSnapshot(IReadOnlySet<string> scripts,
        DateTimeOffset? after, DateTimeOffset? before,
        CancellationToken cancellationToken = default)
    {
        // Default implementation delegates to the non-filtered overload (backwards compatible)
        return GetVtxoByScriptsAsSnapshot(scripts, cancellationToken);
    }

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
    /// Retrieves registered intents by providing a BIP-322 proof of ownership of any input.
    /// </summary>
    Task<ArkIntent[]> GetIntentsByProofAsync(string proof, string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves Arkade transactions the server has registered as pending — i.e. the user
    /// called <see cref="SubmitTx"/> (server locked the inputs as in-flight) but
    /// <see cref="FinalizeTx"/> never followed. The server enforces "you must finalize that
    /// exact pending tx; you cannot spend those inputs another way", so this endpoint is
    /// the only way to recover stranded VTXOs after a crash between Submit and Finalize.
    /// Authentication uses a BIP-322-style proof of ownership over any input the SDK
    /// believes belongs to the wallet — same shape as <see cref="GetIntentsByProofAsync"/>.
    /// </summary>
    Task<Models.PendingArkTransaction[]> GetPendingTxAsync(string proof, string message,
        CancellationToken cancellationToken = default);

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