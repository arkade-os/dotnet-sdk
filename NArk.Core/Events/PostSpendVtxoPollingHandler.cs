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

        if (@event.Psbt is null)
        {
            return;
        }

        var delay = options.Value.TransactionBroadcastPollingDelay;

        // Wait for the configured delay to avoid race conditions with server persistence
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken);
        }

        try
        {
            // Get ALL contracts for affected wallets (not just active ones).
            // Sweep destinations are created as Inactive, so we must poll them too
            // to detect the new VTXOs from the spend transaction.
            

            var scripts = @event.ArkCoins.Select(c => c.ScriptPubKey.ToHex())
                .Concat(@event.Psbt.Outputs.Select(o => o.ScriptPubKey.ToHex()).ToArray()).ToHashSet();

            scripts.Remove("51024e73");
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
