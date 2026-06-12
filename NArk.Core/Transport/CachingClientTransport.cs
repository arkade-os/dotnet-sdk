using Microsoft.Extensions.Logging;
using NArk.Abstractions.Batches;
using NArk.Abstractions.Batches.ServerEvents;
using NArk.Abstractions.Intents;
using NArk.Abstractions.VTXOs;
using NArk.Core.Transport.Models;
using NBitcoin;

namespace NArk.Core.Transport;

/// <summary>
/// Caching decorator for IClientTransport that caches GetServerInfoAsync responses.
/// Server info (network, dust limit, signer key) rarely changes during operation.
/// All other methods are passed through to the underlying transport.
/// </summary>
public class CachingClientTransport : IClientTransport, IServerInfoCacheInvalidation
{
    private readonly IClientTransport _inner;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly TimeSpan _cacheExpiry;
    private readonly TimeSpan _fetchTimeout;

    private ArkServerInfo? _cachedServerInfo;
    private DateTimeOffset _serverInfoExpiresAt;

    public static readonly TimeSpan DefaultCacheExpiry = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultFetchTimeout = TimeSpan.FromSeconds(10);

    public event EventHandler<ServerInfoChangedEventArgs>? ServerInfoChanged;

    public CachingClientTransport(
        IClientTransport inner,
        ILogger<CachingClientTransport>? logger = null,
        TimeSpan? cacheExpiry = null,
        TimeSpan? fetchTimeout = null)
    {
        _inner = inner;
        _logger = logger;
        _cacheExpiry = cacheExpiry ?? DefaultCacheExpiry;
        _fetchTimeout = fetchTimeout ?? DefaultFetchTimeout;
    }

    /// <summary>
    /// Gets server info with caching. Returns cached value if valid, otherwise fetches from server.
    /// </summary>
    public async Task<ArkServerInfo> GetServerInfoAsync(CancellationToken cancellationToken = default)
    {
        // Fast path: return cached if valid
        if (_cachedServerInfo != null && DateTimeOffset.UtcNow < _serverInfoExpiresAt)
        {
            return _cachedServerInfo;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            // Double-check after acquiring lock
            if (_cachedServerInfo != null && DateTimeOffset.UtcNow < _serverInfoExpiresAt)
            {
                return _cachedServerInfo;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_fetchTimeout);

            _logger?.LogDebug("Fetching server info from Ark operator");

            var serverInfo = await _inner.GetServerInfoAsync(cts.Token);

            _cachedServerInfo = serverInfo;
            _serverInfoExpiresAt = DateTimeOffset.UtcNow.Add(_cacheExpiry);

            _logger?.LogDebug("Cached server info: Network={Network}, Dust={Dust}",
                serverInfo.Network.Name, serverInfo.Dust);

            return serverInfo;
        }
        catch (DigestMismatchException)
        {
            InvalidateServerInfoCache(new ServerInfoChangedEventArgs { Reason = ServerInfoChangedReason.DigestMismatch });
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(0, ex, "Failed to fetch server info from Ark operator");

            // Return stale cache if available
            if (_cachedServerInfo != null)
            {
                _logger?.LogInformation("Returning stale cached server info");
                return _cachedServerInfo;
            }

            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Invalidates the server info cache, forcing the next call to fetch fresh data.
    /// Call this on wallet setup/clear or when connection errors occur.
    /// Raises <see cref="ServerInfoChanged"/> so consumers can react (e.g. signer rotation).
    /// </summary>
    public void InvalidateServerInfoCache(ServerInfoChangedEventArgs? args = null)
    {
        _cachedServerInfo = null;
        _serverInfoExpiresAt = DateTimeOffset.MinValue;
        var eventArgs = args ?? new ServerInfoChangedEventArgs();
        _logger?.LogDebug("Server info cache invalidated ({Reason})", eventArgs.Reason);
        ServerInfoChanged?.Invoke(this, eventArgs);
    }

    /// <summary>
    /// Checks if the server info cache currently has valid data.
    /// </summary>
    public bool HasValidServerInfoCache => _cachedServerInfo != null && DateTimeOffset.UtcNow < _serverInfoExpiresAt;

    // Pass-through methods — digest mismatch invalidates the server info cache before propagating.

    public Task<string> SubscribeForScriptsAsync(IReadOnlySet<string> scripts, string? subscriptionId, CancellationToken cancellationToken = default)
        => Guard(() => _inner.SubscribeForScriptsAsync(scripts, subscriptionId, cancellationToken));

    public Task UnsubscribeForScriptsAsync(string subscriptionId, IReadOnlySet<string>? scripts, CancellationToken cancellationToken = default)
        => Guard(() => _inner.UnsubscribeForScriptsAsync(subscriptionId, scripts, cancellationToken));

    public IAsyncEnumerable<HashSet<string>> GetVtxoSubscriptionStreamAsync(string subscriptionId, CancellationToken cancellationToken = default)
        => GuardStream(_inner.GetVtxoSubscriptionStreamAsync(subscriptionId, cancellationToken));

    public IAsyncEnumerable<ArkVtxo> GetVtxoByScriptsAsSnapshot(IReadOnlySet<string> scripts, CancellationToken cancellationToken = default)
        => GuardStream(_inner.GetVtxoByScriptsAsSnapshot(scripts, cancellationToken));

    public IAsyncEnumerable<ArkVtxo> GetVtxoByScriptsAsSnapshot(IReadOnlySet<string> scripts,
        DateTimeOffset? after, DateTimeOffset? before, CancellationToken cancellationToken = default)
        => GuardStream(_inner.GetVtxoByScriptsAsSnapshot(scripts, after, before, cancellationToken));

    public IAsyncEnumerable<ArkVtxo> GetVtxosByOutpoints(IReadOnlyCollection<OutPoint> outpoints, bool spentOnly = false, CancellationToken cancellationToken = default)
        => GuardStream(_inner.GetVtxosByOutpoints(outpoints, spentOnly, cancellationToken));

    public Task<string> RegisterIntent(ArkIntent intent, CancellationToken cancellationToken = default)
        => Guard(() => _inner.RegisterIntent(intent, cancellationToken));

    public Task DeleteIntent(ArkIntent intent, CancellationToken cancellationToken = default)
        => Guard(() => _inner.DeleteIntent(intent, cancellationToken));

    public Task<SubmitTxResponse> SubmitTx(string signedArkTx, string[] checkpointTxs, CancellationToken cancellationToken = default)
        => Guard(() => _inner.SubmitTx(signedArkTx, checkpointTxs, cancellationToken));

    public Task FinalizeTx(string arkTxId, string[] finalCheckpointTxs, CancellationToken cancellationToken)
        => Guard(() => _inner.FinalizeTx(arkTxId, finalCheckpointTxs, cancellationToken));

    public Task SubmitTreeNoncesAsync(SubmitTreeNoncesRequest treeNonces, CancellationToken cancellationToken)
        => Guard(() => _inner.SubmitTreeNoncesAsync(treeNonces, cancellationToken));

    public Task SubmitTreeSignaturesRequest(SubmitTreeSignaturesRequest treeSigs, CancellationToken cancellationToken)
        => Guard(() => _inner.SubmitTreeSignaturesRequest(treeSigs, cancellationToken));

    public Task SubmitSignedForfeitTxsAsync(SubmitSignedForfeitTxsRequest req, CancellationToken cancellationToken)
        => Guard(() => _inner.SubmitSignedForfeitTxsAsync(req, cancellationToken));

    public Task ConfirmRegistrationAsync(string intentId, CancellationToken cancellationToken)
        => Guard(() => _inner.ConfirmRegistrationAsync(intentId, cancellationToken));

    public IAsyncEnumerable<BatchEvent> GetEventStreamAsync(GetEventStreamRequest req, CancellationToken cancellationToken)
        => GuardStream(_inner.GetEventStreamAsync(req, cancellationToken));

    public Task<ArkAssetDetails> GetAssetDetailsAsync(string assetId, CancellationToken cancellationToken = default)
        => Guard(() => _inner.GetAssetDetailsAsync(assetId, cancellationToken));

    public Task UpdateStreamTopicsAsync(string streamId, string[]? addTopics, string[]? removeTopics, CancellationToken cancellationToken = default)
        => Guard(() => _inner.UpdateStreamTopicsAsync(streamId, addTopics, removeTopics, cancellationToken));

    public Task<ArkIntent[]> GetIntentsByProofAsync(string proof, string message, CancellationToken cancellationToken = default)
        => Guard(() => _inner.GetIntentsByProofAsync(proof, message, cancellationToken));

    public Task<Models.PendingArkTransaction[]> GetPendingTxAsync(string proof, string message,
        CancellationToken cancellationToken = default)
        => Guard(() => _inner.GetPendingTxAsync(proof, message, cancellationToken));

    public Task<IReadOnlyList<VtxoChainEntry>> GetVtxoChainAsync(OutPoint vtxoOutpoint, CancellationToken cancellationToken = default)
        => Guard(() => _inner.GetVtxoChainAsync(vtxoOutpoint, cancellationToken));

    public Task<IReadOnlyList<string>> GetVirtualTxsAsync(IReadOnlyList<string> txids, CancellationToken cancellationToken = default)
        => Guard(() => _inner.GetVirtualTxsAsync(txids, cancellationToken));

    public Task<IReadOnlyList<VtxoTreeNode>> GetVtxoTreeAsync(OutPoint batchOutpoint, CancellationToken cancellationToken = default)
        => Guard(() => _inner.GetVtxoTreeAsync(batchOutpoint, cancellationToken));

    private static readonly ServerInfoChangedEventArgs DigestMismatchArgs =
        new() { Reason = ServerInfoChangedReason.DigestMismatch };

    private async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (DigestMismatchException) { InvalidateServerInfoCache(DigestMismatchArgs); throw; }
    }

    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (DigestMismatchException) { InvalidateServerInfoCache(DigestMismatchArgs); throw; }
    }

    private async IAsyncEnumerable<T> GuardStream<T>(IAsyncEnumerable<T> source)
    {
        var e = source.GetAsyncEnumerator();
        await using (e.ConfigureAwait(false))
        {
            while (true)
            {
                bool hasNext;
                try { hasNext = await e.MoveNextAsync(); }
                catch (DigestMismatchException) { InvalidateServerInfoCache(DigestMismatchArgs); throw; }
                if (!hasNext) yield break;
                yield return e.Current;
            }
        }
    }
}
