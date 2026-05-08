using Microsoft.Extensions.Logging;
using NArk.Abstractions.Blockchain;
using NBitcoin.RPC;
using Newtonsoft.Json;

namespace NArk.Blockchain.NBXplorer;

/// <summary>
/// <see cref="IChainTimeProvider"/> backed by Bitcoin Core RPC
/// (typically the NBXplorer-managed node). Caches the last successful
/// chain-time result and falls back to it when a subsequent RPC call
/// fails — Bitcoin Core periodically returns transient 5xx errors during
/// reindex / IBD / heavy load, and a single blip should not be allowed
/// to bubble unhandled exceptions through every caller (which crashes
/// the host process when the provider is consumed by a controller
/// action). The first call after construction must succeed; once cached,
/// the provider is resilient to transient failures.
/// </summary>
public class RPCChainTimeProvider : IChainTimeProvider
{
    private readonly RPCClient _client;
    private readonly ILogger<RPCChainTimeProvider>? _logger;
    private TimeHeight? _lastSuccessful;

    /// <summary>Creates a new provider against the supplied RPC client.</summary>
    /// <param name="client">Configured Bitcoin Core RPC client.</param>
    /// <param name="logger">Optional logger; only used to warn when an
    /// RPC call fails and the cached value is being returned instead.</param>
    public RPCChainTimeProvider(RPCClient client, ILogger<RPCChainTimeProvider>? logger = null)
    {
        _client = client;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// On RPC failure: if a previous call has succeeded since this
    /// provider was constructed the cached <see cref="TimeHeight"/> is
    /// returned and a warning is logged. If there is no cached value
    /// (cold-start failure) the underlying exception propagates.
    /// </remarks>
    public async Task<TimeHeight> GetChainTime(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _client.SendCommandAsync("getblockchaininfo", cancellationToken);
            if (response is null)
                throw new Exception("NBXplorer RPC returned null when retrieving chain information");
            var info = JsonConvert.DeserializeObject<GetBlockchainInfoResponse>(response.ResultString);
            if (info is null)
                throw new Exception("NBXplorer RPC returned invalid json when retrieving chain information");
            var result = new TimeHeight(
                DateTimeOffset.FromUnixTimeSeconds(info.MedianTime),
                info.Blocks
            );
            _lastSuccessful = result;
            return result;
        }
        catch (Exception ex) when (_lastSuccessful is { } cached && ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex,
                "Bitcoin Core RPC getblockchaininfo failed; falling back to cached chain time " +
                "(median={MedianTime}, height={Height}). Caller balances/recoverability classification " +
                "may be slightly stale until the node recovers.",
                cached.Timestamp, cached.Height);
            return cached;
        }
    }

    internal class GetBlockchainInfoResponse
    {
        [JsonProperty("blocks")] public uint Blocks { get; set; }

        [JsonProperty("mediantime")] public long MedianTime { get; set; }
    }
}
