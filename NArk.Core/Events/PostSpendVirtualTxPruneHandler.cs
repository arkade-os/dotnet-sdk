using Microsoft.Extensions.Logging;
using NArk.Core.Enums;
using NArk.Core.Services;

namespace NArk.Core.Events;

/// <summary>
/// After a successful spend, prunes virtual tx data for spent VTXOs.
/// Per-tx storage handles shared nodes naturally — orphan cleanup only
/// removes VirtualTx rows with zero remaining VtxoBranch references.
/// </summary>
public class PostSpendVirtualTxPruneHandler(
    VirtualTxService virtualTxService,
    ILogger<PostSpendVirtualTxPruneHandler>? logger = null
) : IEventHandler<PostCoinsSpendActionEvent>
{
    public async Task HandleAsync(PostCoinsSpendActionEvent @event, CancellationToken cancellationToken = default)
    {
        if (@event.State != ActionState.Successful)
            return;

        try
        {
            var spentOutpoints = @event.ArkCoins.Select(c => c.Outpoint).ToList();
            if (spentOutpoints.Count > 0)
            {
                await virtualTxService.PruneForSpentVtxosAsync(spentOutpoints, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(0, ex,
                "Failed to prune virtual tx data after spend");
        }
    }
}
