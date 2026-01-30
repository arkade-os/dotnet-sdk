using Microsoft.Extensions.Logging;
using NArk.Abstractions.Batches;
using NArk.Abstractions.Batches.ServerEvents;
using NArk.Abstractions.Intents;
using NArk.Abstractions.VTXOs;
using NArk.Core.Transport.Models;

namespace NArk.Core.Transport;

/// <summary>
/// Caching decorator for IClientTransport that caches GetServerInfoAsync responses.
/// Server info (network, dust limit, signer key) rarely changes during operation.
/// All other methods are passed through to the underlying transport.
/// </summary>
public class CachingClientTransport : IClientTransport
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
    /// </summary>
    public void InvalidateServerInfoCache()
    {
        _lock.Wait();
        try
        {
            _cachedServerInfo = null;
            _serverInfoExpiresAt = DateTimeOffset.MinValue;
            _logger?.LogDebug("Server info cache invalidated");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Checks if the server info cache currently has valid data.
    /// </summary>
    public bool HasValidServerInfoCache => _cachedServerInfo != null && DateTimeOffset.UtcNow < _serverInfoExpiresAt;

    // Pass-through methods - no caching needed for these

    public IAsyncEnumerable<HashSet<string>> GetVtxoToPollAsStream(IReadOnlySet<string> scripts, CancellationToken token = default)
        => _inner.GetVtxoToPollAsStream(scripts, token);

    public IAsyncEnumerable<ArkVtxo> GetVtxoByScriptsAsSnapshot(IReadOnlySet<string> scripts, CancellationToken cancellationToken = default)
        => _inner.GetVtxoByScriptsAsSnapshot(scripts, cancellationToken);

    public Task<string> RegisterIntent(ArkIntent intent, CancellationToken cancellationToken = default)
        => _inner.RegisterIntent(intent, cancellationToken);

    public Task DeleteIntent(ArkIntent intent, CancellationToken cancellationToken = default)
        => _inner.DeleteIntent(intent, cancellationToken);

    public Task<SubmitTxResponse> SubmitTx(string signedArkTx, string[] checkpointTxs, CancellationToken cancellationToken = default)
        => _inner.SubmitTx(signedArkTx, checkpointTxs, cancellationToken);

    public Task FinalizeTx(string arkTxId, string[] finalCheckpointTxs, CancellationToken cancellationToken)
        => _inner.FinalizeTx(arkTxId, finalCheckpointTxs, cancellationToken);

    public Task SubmitTreeNoncesAsync(SubmitTreeNoncesRequest treeNonces, CancellationToken cancellationToken)
        => _inner.SubmitTreeNoncesAsync(treeNonces, cancellationToken);

    public Task SubmitTreeSignaturesRequest(SubmitTreeSignaturesRequest treeSigs, CancellationToken cancellationToken)
        => _inner.SubmitTreeSignaturesRequest(treeSigs, cancellationToken);

    public Task SubmitSignedForfeitTxsAsync(SubmitSignedForfeitTxsRequest req, CancellationToken cancellationToken)
        => _inner.SubmitSignedForfeitTxsAsync(req, cancellationToken);

    public Task ConfirmRegistrationAsync(string intentId, CancellationToken cancellationToken)
        => _inner.ConfirmRegistrationAsync(intentId, cancellationToken);

    public IAsyncEnumerable<BatchEvent> GetEventStreamAsync(GetEventStreamRequest req, CancellationToken cancellationToken)
        => _inner.GetEventStreamAsync(req, cancellationToken);
}
