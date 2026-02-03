using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NArk.Abstractions.Contracts;
using NArk.Core.Enums;
using NArk.Core.Models.Options;
using NArk.Core.Services;

namespace NArk.Core.Events;

/// <summary>
/// Event handler that polls for VTXO updates after a successful spend transaction broadcast.
/// This ensures the local VTXO state reflects the new outputs from the transaction.
/// </summary>
public class PostSpendVtxoPollingHandler(
    VtxoSynchronizationService vtxoSyncService,
    IContractStorage contractStorage,
    IOptions<VtxoPollingOptions> options,
    ILogger<PostSpendVtxoPollingHandler>? logger = null
) : IEventHandler<PostCoinsSpendActionEvent>
{
    public async Task HandleAsync(PostCoinsSpendActionEvent @event, CancellationToken cancellationToken = default)
    {
        if (@event.State != ActionState.Successful)
        {
            logger?.LogDebug("Skipping VTXO polling for spend action with state {State}", @event.State);
            return;
        }

        // Get unique wallet IDs from the spent coins
        var walletIds = @event.ArkCoins
            .Select(c => c.WalletIdentifier)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct()
            .ToArray();

        if (walletIds.Length == 0)
        {
            logger?.LogDebug("No wallet IDs found in spent coins, skipping VTXO polling");
            return;
        }

        var delay = options.Value.TransactionBroadcastPollingDelay;

        logger?.LogDebug("Spend transaction {TxId} successful, waiting {DelayMs}ms before polling VTXOs for {WalletCount} wallets",
            @event.TransactionId, delay.TotalMilliseconds, walletIds.Length);

        // Wait for the configured delay to avoid race conditions with server persistence
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken);
        }

        try
        {
            // Get active contracts for all affected wallets
            var contracts = await contractStorage.GetContracts(
                walletIds: walletIds,
                isActive: true,
                cancellationToken: cancellationToken);

            if (contracts.Count == 0)
            {
                logger?.LogDebug("No active contracts found for wallets {WalletIds}, skipping VTXO polling",
                    string.Join(", ", walletIds));
                return;
            }

            var scripts = contracts.Select(c => c.Script).ToHashSet();
            logger?.LogDebug("Polling {ScriptCount} scripts for VTXOs after spend transaction {TxId}",
                scripts.Count, @event.TransactionId);

            await vtxoSyncService.PollScriptsForVtxos(scripts, cancellationToken);

            logger?.LogInformation("VTXO polling completed after spend transaction {TxId}", @event.TransactionId);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(0, ex, "Failed to poll VTXOs after spend transaction {TxId}", @event.TransactionId);
            // Don't rethrow - event handlers shouldn't fail the main flow
        }
    }
}
