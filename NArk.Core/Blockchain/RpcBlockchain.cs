using Microsoft.Extensions.Logging;
using NArk.Abstractions.Blockchain;
using NBitcoin;
using NBitcoin.RPC;
using Newtonsoft.Json;

namespace NArk.Blockchain;

/// <summary>
/// Bitcoin Core RPC-backed <see cref="IBitcoinBlockchain"/>. Use this when the
/// host has direct access to a Bitcoin Core node and doesn't want NBXplorer
/// or Esplora in the middle.
/// <para>
/// <see cref="GetUtxosAsync"/> throws <see cref="NotSupportedException"/> —
/// Bitcoin Core has no native address-indexed UTXO API short of
/// <c>scantxoutset</c> (which is a full UTXO-set scan and impractically
/// expensive for repeated lookups). Pair this backend with NBXplorer or
/// Esplora if you need boarding-UTXO discovery.
/// </para>
/// <para>
/// Chain time uses <c>getblockchaininfo</c> with the same cached-fallback
/// behaviour as the NBXplorer impl — Bitcoin Core periodically returns
/// transient errors during reindex / IBD / heavy load and a single blip
/// shouldn't propagate as an unhandled exception.
/// </para>
/// </summary>
public class RpcBlockchain : IBitcoinBlockchain
{
    private readonly RPCClient _client;
    private readonly ILogger? _logger;
    private TimeHeight? _lastSuccessfulChainTime;

    public RpcBlockchain(RPCClient client, ILogger<RpcBlockchain>? logger = null)
    {
        _client = client;
        _logger = logger;
    }

    // ── Chain time ───────────────────────────────────────────────────

    public async Task<TimeHeight> GetChainTime(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _client.SendCommandAsync("getblockchaininfo", cancellationToken);
            if (response is null)
                throw new Exception("Bitcoin Core RPC returned null for getblockchaininfo");
            var info = JsonConvert.DeserializeObject<GetBlockchainInfoResponse>(response.ResultString)
                ?? throw new Exception("Bitcoin Core RPC returned invalid JSON for getblockchaininfo");
            var result = new TimeHeight(
                DateTimeOffset.FromUnixTimeSeconds(info.MedianTime),
                info.Blocks);
            _lastSuccessfulChainTime = result;
            return result;
        }
        catch (Exception ex) when (_lastSuccessfulChainTime is { } cached && ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex,
                "Bitcoin Core RPC getblockchaininfo failed; falling back to cached chain time " +
                "(median={MedianTime}, height={Height}).",
                cached.Timestamp, cached.Height);
            return cached;
        }
    }

    // ── UTXO lookup (not supported) ──────────────────────────────────

    public Task<IReadOnlyList<BoardingUtxo>> GetUtxosAsync(string address, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "Bitcoin Core RPC has no native address-indexed UTXO API. " +
            "Use NBXplorerBlockchain or EsploraBlockchain when boarding-UTXO discovery is required.");

    // ── Broadcast (sendrawtransaction + submitpackage) ───────────────

    public async Task<bool> BroadcastAsync(Transaction tx, CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.SendCommandAsync("sendrawtransaction", cancellationToken, tx.ToHex());
            return true;
        }
        catch (RPCException ex)
        {
            _logger?.LogWarning("RPC broadcast rejected tx {Txid}: {Error}", tx.GetHash(), ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(0, ex, "RPC broadcast failed for tx {Txid}", tx.GetHash());
            return false;
        }
    }

    public async Task<bool> BroadcastPackageAsync(Transaction parent, Transaction child, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _client.SendCommandAsync(
                "submitpackage",
                cancellationToken,
                new object[] { new[] { parent.ToHex(), child.ToHex() } });

            if (response.Error is not null)
            {
                _logger?.LogWarning("submitpackage failed: {Error}", response.Error.Message);
                return await BroadcastSequentialFallbackAsync(parent, child, cancellationToken);
            }
            return true;
        }
        catch (Exception ex)
        {
            // submitpackage requires Bitcoin Core 28+; fall back to sequential.
            _logger?.LogWarning(0, ex,
                "submitpackage failed for parent {Txid}, falling back to sequential broadcast",
                parent.GetHash());
            return await BroadcastSequentialFallbackAsync(parent, child, cancellationToken);
        }
    }

    private async Task<bool> BroadcastSequentialFallbackAsync(Transaction parent, Transaction child, CancellationToken ct)
    {
        var parentOk = await BroadcastAsync(parent, ct);
        if (!parentOk) return false;
        var childOk = await BroadcastAsync(child, ct);
        if (!childOk)
            _logger?.LogDebug("Sequential fallback: child CPFP broadcast failed, but parent was accepted");
        return true;
    }

    // ── Tx status ────────────────────────────────────────────────────

    public async Task<TxStatus> GetTxStatusAsync(uint256 txid, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _client.SendCommandAsync(
                "getrawtransaction", cancellationToken, txid.ToString(), true);

            if (response.Error is not null)
                return new TxStatus(false, null, false);

            var confirmations = (int?)response.Result?["confirmations"] ?? 0;
            var blockHeight = (uint?)(long?)response.Result?["blockheight"];

            if (confirmations > 0)
                return new TxStatus(true, blockHeight, false);

            return new TxStatus(false, null, true); // In mempool
        }
        catch
        {
            return new TxStatus(false, null, false); // Unknown
        }
    }

    // ── Fee estimate ─────────────────────────────────────────────────

    public async Task<FeeRate> EstimateFeeRateAsync(int confirmTarget = 6, CancellationToken cancellationToken = default)
    {
        try
        {
            var estimate = await _client.EstimateSmartFeeAsync(confirmTarget);
            return estimate.FeeRate ?? new FeeRate(Money.Satoshis(2));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(0, ex, "estimatesmartfee failed, using fallback");
            return new FeeRate(Money.Satoshis(2));
        }
    }

    private class GetBlockchainInfoResponse
    {
        [JsonProperty("blocks")] public uint Blocks { get; set; }
        [JsonProperty("mediantime")] public long MedianTime { get; set; }
    }
}
