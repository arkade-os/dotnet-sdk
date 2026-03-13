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
            

            var inputScripts = @event.ArkCoins.Select(c => c.ScriptPubKey.ToHex()).ToHashSet();
            var inputOutpoints = @event.ArkCoins.Select(c => c.Outpoint).ToHashSet();
            var outputScripts = @event.Psbt.Outputs.Select(o => o.ScriptPubKey.ToHex()).ToHashSet();
            outputScripts.Remove("51024e73");

            var scripts = inputScripts.Union(outputScripts).ToHashSet();

            logger?.LogInformation(
                "PostSpendVtxoPolling: TxId={TxId}, delay={Delay}ms, inputScripts=[{InputScripts}], outputScripts=[{OutputScripts}]",
                @event.TransactionId, delay.TotalMilliseconds,
                string.Join(", ", inputScripts),
                string.Join(", ", outputScripts));

            // Retry with backoff — arkd's indexer may not have processed the VTXOs yet
            // We must verify that input VTXOs are marked as spent, not just that any VTXO was found.
            // Breaking early on `found > 0` caused input VTXOs to remain "unspent" locally
            // when arkd returned the new output VTXOs before updating the spent state of inputs.
            const int maxAttempts = 5;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var found = await vtxoSyncService.PollScriptsForVtxos(scripts, cancellationToken);
                logger?.LogInformation(
                    "PostSpendVtxoPolling: attempt {Attempt}/{Max} for TxId={TxId}, {Found} VTXOs returned",
                    attempt, maxAttempts, @event.TransactionId, found);

                if (found > 0)
                {
                    // Verify input VTXOs are now marked as spent in local storage
                    var inputVtxos = await vtxoSyncService.GetVtxosByOutpoints(inputOutpoints, cancellationToken);
                    var allInputsSpent = inputVtxos.Count > 0 && inputVtxos.All(v => v.IsSpent());
                    if (allInputsSpent)
                        break;

                    logger?.LogInformation(
                        "PostSpendVtxoPolling: attempt {Attempt}/{Max} for TxId={TxId} — output VTXOs found but {Unspent} input(s) still unspent",
                        attempt, maxAttempts, @event.TransactionId,
                        inputVtxos.Count(v => !v.IsSpent()));
                }

                if (attempt < maxAttempts)
                    await Task.Delay(delay, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(0, ex, "Failed to poll VTXOs after spend transaction {TxId}", @event.TransactionId);
            // Don't rethrow - event handlers shouldn't fail the main flow
        }
    }
}
