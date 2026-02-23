using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NArk.Swaps.Boltz.Models;
using NArk.Swaps.Boltz.Models.Swaps.Chain;
using NArk.Swaps.Boltz.Models.Swaps.Reverse;
using NArk.Swaps.Boltz.Models.Swaps.Submarine;

namespace NArk.Swaps.Boltz.Client;

/// <summary>
/// BoltzClient with caching for pairs (limits/fees) responses.
/// Inherits from BoltzClient and overrides GetSubmarinePairsAsync and GetReversePairsAsync
/// to add caching. All other methods use base class implementation.
/// </summary>
public class CachedBoltzClient : BoltzClient
{
    private readonly ILogger<CachedBoltzClient>? _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private SubmarinePairsResponse? _submarineCache;
    private ReversePairsResponse? _reverseCache;
    private ChainPairsResponse? _chainCache;
    private bool _chainFetched;
    private DateTimeOffset _expiresAt;

    private static readonly TimeSpan CacheExpiry = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(5);

    public CachedBoltzClient(
        HttpClient httpClient,
        IOptions<BoltzClientOptions> options,
        ILogger<CachedBoltzClient>? logger = null)
        : base(httpClient, options)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets cached submarine pairs response, fetching from Boltz if cache is expired.
    /// </summary>
    public override async Task<SubmarinePairsResponse?> GetSubmarinePairsAsync(CancellationToken cancellation = default)
    {
        await EnsureCacheFreshAsync(cancellation);
        return _submarineCache;
    }

    /// <summary>
    /// Gets cached reverse pairs response, fetching from Boltz if cache is expired.
    /// </summary>
    public override async Task<ReversePairsResponse?> GetReversePairsAsync(CancellationToken cancellation = default)
    {
        await EnsureCacheFreshAsync(cancellation);
        return _reverseCache;
    }

    /// <summary>
    /// Gets cached chain pairs response, fetching from Boltz if cache is expired.
    /// </summary>
    public override async Task<ChainPairsResponse?> GetChainPairsAsync(CancellationToken cancellation = default)
    {
        await EnsureCacheFreshAsync(cancellation);
        return _chainCache;
    }

    /// <summary>
    /// Ensures the cache is fresh, fetching from Boltz API if needed.
    /// </summary>
    private async Task EnsureCacheFreshAsync(CancellationToken cancellation)
    {
        // Fast path: cache is still valid
        if (_submarineCache != null && _reverseCache != null && _chainFetched && DateTimeOffset.UtcNow < _expiresAt)
        {
            return;
        }

        await _lock.WaitAsync(cancellation);
        try
        {
            // Double-check after acquiring lock
            if (_submarineCache != null && _reverseCache != null && _chainFetched && DateTimeOffset.UtcNow < _expiresAt)
            {
                return;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            cts.CancelAfter(FetchTimeout);

            _logger?.LogDebug("Fetching Boltz pairs from API");

            // Fetch all in parallel using base class methods
            var submarineTask = base.GetSubmarinePairsAsync(cts.Token);
            var reverseTask = base.GetReversePairsAsync(cts.Token);
            // Chain pairs may not be supported — fetch independently
            var chainTask = FetchChainPairsSafeAsync(cts.Token);

            await Task.WhenAll(submarineTask, reverseTask, chainTask);

            _submarineCache = await submarineTask;
            _reverseCache = await reverseTask;
            _chainCache = await chainTask;
            _chainFetched = true;
            _expiresAt = DateTimeOffset.UtcNow.Add(CacheExpiry);

            _logger?.LogDebug(
                "Cached Boltz pairs - Submarine: {SubMin}-{SubMax} sats, Reverse: {RevMin}-{RevMax} sats, Chain: available={ChainAvailable}",
                _submarineCache?.ARK?.BTC?.Limits?.Minimal,
                _submarineCache?.ARK?.BTC?.Limits?.Maximal,
                _reverseCache?.BTC?.ARK?.Limits?.Minimal,
                _reverseCache?.BTC?.ARK?.Limits?.Maximal,
                _chainCache != null);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Fetches chain pairs without failing if the endpoint is not supported.
    /// </summary>
    private async Task<ChainPairsResponse?> FetchChainPairsSafeAsync(CancellationToken cancellation)
    {
        try
        {
            return await base.GetChainPairsAsync(cancellation);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Chain swap pairs not available from Boltz");
            return null;
        }
    }

    /// <summary>
    /// Invalidates the cache, forcing the next call to fetch fresh data.
    /// </summary>
    public void Invalidate()
    {
        _lock.Wait();
        try
        {
            _submarineCache = null;
            _reverseCache = null;
            _chainCache = null;
            _chainFetched = false;
            _expiresAt = DateTimeOffset.MinValue;
            _logger?.LogDebug("Boltz pairs cache invalidated");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Checks if the cache currently has valid data.
    /// </summary>
    public bool HasValidCache => _submarineCache != null && _reverseCache != null && _chainFetched && DateTimeOffset.UtcNow < _expiresAt;
}
