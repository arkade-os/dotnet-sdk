using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NArk.Abstractions.Contracts;
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

            await vtxoSyncService.PollScriptsForVtxos(scripts, cancellationToken);

            logger?.LogInformation("VTXO polling completed after batch success for wallet {WalletId}", walletId);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(0, ex, "Failed to poll VTXOs after batch success for wallet {WalletId}", walletId);
            // Don't rethrow - event handlers shouldn't fail the main flow
        }
    }
}
