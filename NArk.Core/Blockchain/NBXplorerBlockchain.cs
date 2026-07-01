using Microsoft.Extensions.Logging;
using NArk.Abstractions.Blockchain;
using NBitcoin;
using NBXplorer;
using NBXplorer.Models;
using Newtonsoft.Json;

namespace NArk.Blockchain;

/// <summary>
/// Unified NBXplorer-backed <see cref="IBitcoinBlockchain"/>. Address-indexed
/// UTXO lookup goes through NBXplorer's TrackedSource API; everything else
/// (chain time, broadcast, tx status, fee estimate) goes through the underlying
/// Bitcoin Core RPC client that NBXplorer exposes via <see cref="ExplorerClient.RPCClient"/>.
/// <para>
/// Caches the last successful chain-time result so a single Bitcoin Core RPC blip
/// during reindex / IBD / heavy load doesn't bubble up as an unhandled exception
/// through every chain-time-dependent caller. The first call after construction
/// must succeed; once cached, the provider is resilient to transient failures.
/// </para>
/// </summary>
public class NBXplorerBlockchain : IBitcoinBlockchain
{
    private readonly ExplorerClient _explorerClient;
    private readonly ILogger? _logger;
    private TimeHeight? _lastSuccessfulChainTime;

    public NBXplorerBlockchain(ExplorerClient explorerClient, ILogger<NBXplorerBlockchain>? logger = null)
    {
        _explorerClient = explorerClient;
        _logger = logger;
    }

    public NBXplorerBlockchain(Network network, Uri nbxplorerUri, ILogger<NBXplorerBlockchain>? logger = null)
        : this(new ExplorerClient(new NBXplorerNetworkProvider(network.ChainName).GetBTC(), nbxplorerUri), logger)
    {
    }

    // ── Chain time (RPC getblockchaininfo + cached-fallback) ────────

    public async Task<TimeHeight> GetChainTime(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _explorerClient.RPCClient.SendCommandAsync("getblockchaininfo", cancellationToken);
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
                "(median={MedianTime}, height={Height}). Caller balances/recoverability classification " +
                "may be slightly stale until the node recovers.",
                cached.Timestamp, cached.Height);
            return cached;
        }
    }

    // ── UTXO lookup (NBXplorer TrackedSource) ───────────────────────

    public async Task<IReadOnlyList<BoardingUtxo>> GetUtxosAsync(string address, CancellationToken cancellationToken = default)
    {
        var bitcoinAddress = BitcoinAddress.Create(address, _explorerClient.Network.NBitcoinNetwork);
        var trackedSource = TrackedSource.Create(bitcoinAddress);

        // Ensure the address is tracked (idempotent)
        await _explorerClient.TrackAsync(trackedSource, cancellation: cancellationToken);

        var utxoChanges = await _explorerClient.GetUTXOsAsync(trackedSource, cancellationToken);
        // GetUnspentUTXOs applies the spent filter across confirmed + unconfirmed deltas.
        var unspent = utxoChanges.GetUnspentUTXOs().ToList();

        var results = new List<BoardingUtxo>(unspent.Count);
        foreach (var utxo in unspent)
        {
            var confirmed = utxo.Confirmations > 0;
            var blockHeight = confirmed
                ? utxoChanges.CurrentHeight - (int)utxo.Confirmations + 1
                : 0;

            results.Add(new BoardingUtxo(
                Txid: utxo.Outpoint.Hash.ToString(),
                Vout: (uint)utxo.Outpoint.N,
                Amount: ((Money)utxo.Value).Satoshi > 0 ? (ulong)((Money)utxo.Value).Satoshi : 0,
                Confirmed: confirmed,
                BlockHeight: blockHeight,
                BlockTime: confirmed ? utxo.Timestamp.ToUnixTimeSeconds() : 0));
        }

        return results;
    }

    // ── Broadcast + package (NBXplorer BroadcastAsync + RPC submitpackage) ──

    public async Task<bool> BroadcastAsync(Transaction tx, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _explorerClient.BroadcastAsync(tx, cancellationToken);
            if (!result.Success)
                _logger?.LogWarning("Broadcast failed for tx {Txid}: {Error}", tx.GetHash(), result.RPCMessage);
            return result.Success;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(0, ex, "Failed to broadcast tx {Txid}", tx.GetHash());
            return false;
        }
    }

    public async Task<bool> BroadcastPackageAsync(Transaction parent, Transaction child, CancellationToken cancellationToken = default)
    {
        try
        {
            // maxfeerate=0 (unlimited) + maxburnamount=21e6 so Ark P2A anchor
            // outputs (non-zero value, non-standard script) are not rejected by
            // Bitcoin Core's burn-output check (default maxburnamount=0).
            var response = await _explorerClient.RPCClient.SendCommandAsync(
                "submitpackage",
                cancellationToken,
                new[] { parent.ToHex(), child.ToHex() },
                0m,
                21_000_000m);

            if (response.Error is not null)
            {
                _logger?.LogWarning("submitpackage failed: {Error}", response.Error.Message);
                return await BroadcastSequentialFallbackAsync(parent, child, cancellationToken);
            }

            _logger?.LogDebug("Package broadcast successful: parent={Parent}, child={Child}",
                parent.GetHash(), child.GetHash());
            return true;
        }
        catch (Exception ex)
        {
            // submitpackage RPC may not exist on Bitcoin Core < 28 — fall back to sequential
            _logger?.LogWarning(0, ex,
                "submitpackage failed for parent {Txid}, falling back to sequential broadcast", parent.GetHash());
            return await BroadcastSequentialFallbackAsync(parent, child, cancellationToken);
        }
    }

    private async Task<bool> BroadcastSequentialFallbackAsync(Transaction parent, Transaction child, CancellationToken ct)
    {
        // Use direct RPC for the parent so we can pass maxburnamount — NBXplorer's
        // HTTP broadcast endpoint does not expose that parameter.
        var parentOk = await BroadcastRpcAsync(parent, ct);
        if (!parentOk) return false;
        var childOk = await BroadcastAsync(child, ct);
        if (!childOk)
            _logger?.LogDebug("Sequential fallback: child CPFP broadcast failed, but parent was accepted");
        return true;
    }

    // Broadcasts via direct Bitcoin Core RPC with maxburnamount set so Ark P2A
    // anchor outputs are not rejected by the burn-output check.
    private async Task<bool> BroadcastRpcAsync(Transaction tx, CancellationToken ct)
    {
        try
        {
            var response = await _explorerClient.RPCClient.SendCommandAsync(
                "sendrawtransaction", ct,
                tx.ToHex(),
                0m,
                21_000_000m);
            if (response.Error is not null)
            {
                _logger?.LogWarning("Broadcast failed for tx {Txid}: {Error}", tx.GetHash(), response.Error.Message);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(0, ex, "Failed to broadcast tx {Txid}", tx.GetHash());
            return false;
        }
    }

    // ── Tx status (RPC getrawtransaction) ────────────────────────────

    public async Task<TxStatus> GetTxStatusAsync(uint256 txid, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _explorerClient.RPCClient.SendCommandAsync(
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

    // ── Fee estimate (RPC estimatesmartfee) ──────────────────────────

    public async Task<FeeRate> EstimateFeeRateAsync(int confirmTarget = 6, CancellationToken cancellationToken = default)
    {
        try
        {
            var estimate = await _explorerClient.RPCClient.EstimateSmartFeeAsync(confirmTarget);
            return estimate.FeeRate ?? new FeeRate(Money.Satoshis(2)); // Fallback to 2 sat/vB
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(0, ex, "Failed to estimate fee rate, using fallback");
            return new FeeRate(Money.Satoshis(2));
        }
    }

    private class GetBlockchainInfoResponse
    {
        [JsonProperty("blocks")] public uint Blocks { get; set; }
        [JsonProperty("mediantime")] public long MedianTime { get; set; }
    }
}
