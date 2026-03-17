using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VirtualTxs;
using NArk.Abstractions.VTXOs;
using NArk.Core.Enums;
using NArk.Core.Models.Options;
using NArk.Core.Services;
using NBitcoin;

namespace NArk.Core.Events;

/// <summary>
/// After a successful batch session, fetches virtual tx branch data for new VTXOs.
/// This ensures exit data is ready for unilateral exit if needed.
/// </summary>
public class PostBatchVirtualTxFetchHandler(
    VirtualTxService virtualTxService,
    IVtxoStorage vtxoStorage,
    IContractStorage contractStorage,
    IOptions<VirtualTxOptions> options,
    ILogger<PostBatchVirtualTxFetchHandler>? logger = null
) : IEventHandler<PostBatchSessionEvent>
{
    public async Task HandleAsync(PostBatchSessionEvent @event, CancellationToken cancellationToken = default)
    {
        if (@event.State != ActionState.Successful)
            return;

        var mode = options.Value.DefaultMode;
        var minAmount = options.Value.MinExitWorthAmount;

        try
        {
            // Wait a bit for VTXO polling to complete first
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

            var walletId = @event.Intent.WalletId;

            // Get active contracts for the wallet
            var contracts = await contractStorage.GetContracts(
                walletIds: [walletId],
                isActive: true,
                cancellationToken: cancellationToken);

            if (contracts.Count == 0)
                return;

            var scripts = contracts.Select(c => c.Script).ToArray();

            // Get unspent VTXOs for these scripts
            var vtxos = await vtxoStorage.GetVtxos(
                scripts: scripts,
                cancellationToken: cancellationToken);

            var unspentVtxos = vtxos
                .Where(v => !v.IsSpent() && v.Amount >= minAmount)
                .ToList();

            foreach (var vtxo in unspentVtxos)
            {
                try
                {
                    await virtualTxService.FetchAndStoreBranchAsync(
                        vtxo.OutPoint, mode, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(0, ex,
                        "Failed to fetch virtual tx branch for VTXO {Outpoint}", vtxo.OutPoint);
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(0, ex,
                "Failed to fetch virtual tx data after batch session");
        }
    }
}
