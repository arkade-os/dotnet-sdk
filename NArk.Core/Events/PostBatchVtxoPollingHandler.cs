using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Core.Enums;
using NArk.Core.Models.Options;
using NArk.Core.Services;

namespace NArk.Core.Events;

/// <summary>
/// Event handler that polls for VTXO updates after a successful batch session.
/// This ensures the local VTXO state is updated with the new outputs from the batch.
/// </summary>
public class PostBatchVtxoPollingHandler(
    VtxoSynchronizationService vtxoSyncService,
    IContractStorage contractStorage,
    IVtxoStorage vtxoStorage,
    IOptions<VtxoPollingOptions> options,
    ILogger<PostBatchVtxoPollingHandler>? logger = null
) : IEventHandler<PostBatchSessionEvent>
{
    public async Task HandleAsync(PostBatchSessionEvent @event, CancellationToken cancellationToken = default)
    {
        if (@event.State != ActionState.Successful)
        {
            logger?.LogDebug("Skipping VTXO polling for batch session with state {State}", @event.State);
            return;
        }

        var walletId = @event.Intent.WalletId;
        var delay = options.Value.BatchSuccessPollingDelay;

        logger?.LogDebug("Batch session successful for wallet {WalletId}, waiting {DelayMs}ms before polling VTXOs",
            walletId, delay.TotalMilliseconds);

        // Wait for the configured delay to avoid race conditions with server persistence
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken);
        }

        try
        {
            // Get active contracts for the wallet
            var contracts = await contractStorage.GetContracts(
                walletIds: [walletId],
                isActive: true,
                cancellationToken: cancellationToken);

            if (contracts.Count == 0)
            {
                logger?.LogDebug("No active contracts found for wallet {WalletId}, skipping VTXO polling", walletId);
                return;
            }

            var scripts = contracts.Select(c => c.Script).ToHashSet();
            logger?.LogDebug("Polling {ScriptCount} scripts for VTXOs after batch success for wallet {WalletId}",
                scripts.Count, walletId);

            // Time-filter the poll so wallets with large historical VTXO counts
            // don't re-fetch the whole script history on every batch success.
            var after = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5);
            await vtxoSyncService.PollScriptsForVtxos(scripts, after, cancellationToken);

            // Mark unrolled inputs (boarding UTXOs) as spent by the commitment tx.
            // These are on-chain UTXOs not tracked by arkd — we know the spending tx directly.
            if (@event.Intent.IntentVtxos.Length > 0 && @event.CommitmentTransactionId is not null)
            {
                var inputVtxos = await vtxoStorage.GetVtxos(
                    outpoints: @event.Intent.IntentVtxos,
                    includeSpent: false,
                    cancellationToken: cancellationToken);

                foreach (var vtxo in inputVtxos.Where(v => v.Unrolled))
                {
                    logger?.LogInformation(
                        "Marking unrolled VTXO {Outpoint} as spent by commitment tx {CommitmentTxId}",
                        vtxo.OutPoint, @event.CommitmentTransactionId);
                    await vtxoStorage.UpsertVtxo(
                        vtxo with { SpentByTransactionId = @event.CommitmentTransactionId },
                        cancellationToken);
                }
            }

            logger?.LogInformation("VTXO polling completed after batch success for wallet {WalletId}", walletId);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(0, ex, "Failed to poll VTXOs after batch success for wallet {WalletId}", walletId);
            // Don't rethrow - event handlers shouldn't fail the main flow
        }
    }
}
