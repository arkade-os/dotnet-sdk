using Microsoft.Extensions.Logging;
using NArk.Abstractions.Blockchain;
using NBitcoin;
using NBXplorer;

namespace NArk.Blockchain.NBXplorer;

/// <summary>
/// Broadcasts transactions via NBXplorer (which wraps Bitcoin Core RPC).
/// Supports single tx broadcast and 1p1c package relay via submitpackage.
/// </summary>
public class NBXplorerOnchainBroadcaster(
    ExplorerClient explorerClient,
    ILogger<NBXplorerOnchainBroadcaster>? logger = null)
    : IOnchainBroadcaster
{
    public async Task<bool> BroadcastAsync(Transaction tx, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await explorerClient.BroadcastAsync(tx, cancellationToken);
            if (!result.Success)
            {
                logger?.LogWarning("Broadcast failed for tx {Txid}: {Error}",
                    tx.GetHash(), result.RPCMessage);
            }
            return result.Success;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(0, ex, "Failed to broadcast tx {Txid}", tx.GetHash());
            return false;
        }
    }

    public async Task<bool> BroadcastPackageAsync(
        Transaction parent, Transaction child, CancellationToken cancellationToken = default)
    {
        try
        {
            var parentHex = parent.ToHex();
            var childHex = child.ToHex();

            var response = await explorerClient.RPCClient.SendCommandAsync(
                "submitpackage",
                cancellationToken,
                new object[] { new[] { parentHex, childHex } });

            if (response.Error is not null)
            {
                logger?.LogWarning("submitpackage failed: {Error}", response.Error.Message);
                // Fall back to sequential broadcast (submitpackage requires Bitcoin Core 28+)
                return await BroadcastSequentialFallbackAsync(parent, child, cancellationToken);
            }

            logger?.LogDebug("Package broadcast successful: parent={Parent}, child={Child}",
                parent.GetHash(), child.GetHash());
            return true;
        }
        catch (Exception ex)
        {
            // submitpackage RPC may not exist on Bitcoin Core < 28 — fall back to sequential
            logger?.LogWarning(0, ex,
                "submitpackage failed for parent {Txid}, falling back to sequential broadcast", parent.GetHash());
            return await BroadcastSequentialFallbackAsync(parent, child, cancellationToken);
        }
    }

    private async Task<bool> BroadcastSequentialFallbackAsync(
        Transaction parent, Transaction child, CancellationToken cancellationToken)
    {
        var parentOk = await BroadcastAsync(parent, cancellationToken);
        if (!parentOk)
            return false;

        // Parent accepted — try child CPFP, but parent success is the minimum requirement
        var childOk = await BroadcastAsync(child, cancellationToken);
        if (!childOk)
            logger?.LogDebug("Sequential fallback: child CPFP broadcast failed, but parent was accepted");

        return true;
    }

    public async Task<TxStatus> GetTxStatusAsync(
        uint256 txid, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await explorerClient.RPCClient.SendCommandAsync(
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

    public async Task<FeeRate> EstimateFeeRateAsync(
        int confirmTarget = 6, CancellationToken cancellationToken = default)
    {
        try
        {
            var estimate = await explorerClient.RPCClient.EstimateSmartFeeAsync(confirmTarget);
            return estimate.FeeRate ?? new FeeRate(Money.Satoshis(2)); // Fallback to 2 sat/vB
        }
        catch (Exception ex)
        {
            logger?.LogWarning(0, ex, "Failed to estimate fee rate, using fallback");
            return new FeeRate(Money.Satoshis(2));
        }
    }
}
