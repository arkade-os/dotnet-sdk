using System.Runtime.CompilerServices;
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
    /// Creates a new VTXO-script subscription (when <paramref name="subscriptionId"/> is null
    /// or empty) or adds scripts to an existing one. Returns the subscription id to open the
    /// stream with via <see cref="GetVtxoSubscriptionStreamAsync"/>.
    /// <para>
    /// arkd's subscription is mutable while its stream is open: scripts added here are routed
    /// onto the already-open stream, so the watched set can be updated in place without tearing
    /// the stream down and resubscribing.
    /// </para>
    /// </summary>
    Task<string> SubscribeForScriptsAsync(IReadOnlySet<string> scripts, string? subscriptionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes scripts from an existing subscription. When <paramref name="scripts"/> is null or
    /// empty, all scripts are removed and the subscription is torn down server-side.
    /// </summary>
    Task UnsubscribeForScriptsAsync(string subscriptionId, IReadOnlySet<string>? scripts,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the long-lived server stream for a subscription, yielding the set of scripts whose
    /// VTXOs changed on each push. The subscription's script set may be mutated in place via
    /// <see cref="SubscribeForScriptsAsync"/> / <see cref="UnsubscribeForScriptsAsync"/> while
    /// this stream stays open.
    /// </summary>
    IAsyncEnumerable<HashSet<string>> GetVtxoSubscriptionStreamAsync(string subscriptionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Convenience: create a one-shot subscription for <paramref name="scripts"/> and stream it.
    /// Built on the composable primitives above; it does not surface the subscription id, so it
    /// cannot update the watched set in place — prefer the primitives when the set changes over
    /// time (see <see cref="SubscribeForScriptsAsync"/>).
    /// </summary>
    async IAsyncEnumerable<HashSet<string>> GetVtxoToPollAsStream(IReadOnlySet<string> scripts,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        var subscriptionId = await SubscribeForScriptsAsync(scripts, subscriptionId: null, token);
        await foreach (var changed in GetVtxoSubscriptionStreamAsync(subscriptionId, token))
            yield return changed;
    }

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