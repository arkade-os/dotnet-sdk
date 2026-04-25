using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NArk.Core.Enums;
using NArk.Core.Models.Options;
using NArk.Core.Services;
using NArk.Core.Transport;

namespace NArk.Core.Events;

/// <summary>
/// Event handler that polls for VTXO updates after a successful spend transaction broadcast.
/// This ensures the local VTXO state reflects the new outputs from the transaction.
/// </summary>
public class PostSpendVtxoPollingHandler(
    VtxoSynchronizationService vtxoSyncService,
    IClientTransport transport,
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
            var inputScripts = @event.ArkCoins.Select(c => c.ScriptPubKey.ToHex()).ToHashSet();
            var inputOutpoints = @event.ArkCoins.Select(c => c.Outpoint).ToList();
            var outputScripts = @event.Psbt.Outputs.Select(o => o.ScriptPubKey.ToHex()).ToHashSet();
            outputScripts.Remove("51024e73");

            var scripts = inputScripts.Union(outputScripts).ToHashSet();

            logger?.LogInformation(
                "PostSpendVtxoPolling: TxId={TxId}, delay={Delay}ms, inputScripts=[{InputScripts}], outputScripts=[{OutputScripts}]",
                @event.TransactionId, delay.TotalMilliseconds,
                string.Join(", ", inputScripts),
                string.Join(", ", outputScripts));

            // Retry with backoff — arkd's indexer may not have processed the VTXOs yet.
            // We poll all scripts to upsert both input (spent) and output (new) VTXOs,
            // then use the transport's spent_only filter to verify inputs are marked spent
            // by arkd before breaking — avoids relying on local storage which may lag.
            //
            // Time-filter: only fetch VTXOs updated within a short window before the
            // spend. Scripts that already hold large history would otherwise re-fetch
            // everything on every spend, which is what triggered this optimisation.
            var after = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5);
            const int maxAttempts = 5;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var found = await vtxoSyncService.PollScriptsForVtxos(scripts, after, cancellationToken);
                logger?.LogInformation(
                    "PostSpendVtxoPolling: attempt {Attempt}/{Max} for TxId={TxId}, {Found} VTXOs returned",
                    attempt, maxAttempts, @event.TransactionId, found);

                if (found > 0)
                {
                    // Ask arkd directly: are the input outpoints spent?
                    var spentCount = 0;
                    await foreach (var _ in transport.GetVtxosByOutpoints(inputOutpoints, spentOnly: true, cancellationToken))
                        spentCount++;

                    if (spentCount >= inputOutpoints.Count)
                        break;

                    logger?.LogInformation(
                        "PostSpendVtxoPolling: attempt {Attempt}/{Max} for TxId={TxId} — {Spent}/{Total} inputs spent on arkd",
                        attempt, maxAttempts, @event.TransactionId,
                        spentCount, inputOutpoints.Count);
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
