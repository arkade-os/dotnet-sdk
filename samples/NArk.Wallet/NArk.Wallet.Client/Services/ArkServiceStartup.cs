using Microsoft.Extensions.Logging;
using NArk.Core.Services;
using NArk.Swaps.Services;

namespace NArk.Wallet.Client.Services;

/// <summary>
/// Extension to manually start SDK background services in Blazor WASM
/// (which doesn't support IHostedService).
/// </summary>
public static class ArkServiceStartup
{
    public static async Task StartArkServicesAsync(this IServiceProvider services)
    {
        var cts = new CancellationTokenSource();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("ArkServiceStartup");

        // Start services in the same order as ArkHostedLifecycle
        var sweeper = services.GetRequiredService<SweeperService>();
        await sweeper.StartAsync(cts.Token);

        var batch = services.GetRequiredService<BatchManagementService>();
        await batch.StartAsync(cts.Token);

        var intentSync = services.GetRequiredService<IntentSynchronizationService>();
        await intentSync.StartAsync(cts.Token);

        var intentGen = services.GetRequiredService<IntentGenerationService>();
        await intentGen.StartAsync(cts.Token);

        // Non-fatal if server subscription endpoint is unavailable (e.g. 500/501) —
        // VtxoSynchronizationService will fall back to routine polling.
        try
        {
            var vtxoSync = services.GetRequiredService<VtxoSynchronizationService>();
            await vtxoSync.StartAsync(cts.Token);
        }
        catch (Exception ex) { logger.LogWarning(ex, "VtxoSynchronizationService failed to start — falling back to polling"); }

        // Start swap management (monitors swap status, handles claims).
        // Non-fatal if Boltz is unreachable — swaps just won't be monitored until next app load.
        try
        {
            var swapMgr = services.GetRequiredService<SwapsManagementService>();
            await swapMgr.StartAsync(cts.Token);
        }
        catch (Exception ex) { logger.LogWarning(ex, "SwapsManagementService failed to start"); }

        // Start Arkade asset-swap monitoring (transitions covenant swaps: filled by a solver / cancelled).
        try
        {
            var arkadeSwaps = services.GetRequiredService<NArk.ArkadeIntents.Services.ArkadeSwapIntentMonitoringService>();
            await arkadeSwaps.StartAsync(cts.Token);
        }
        catch (Exception ex) { logger.LogWarning(ex, "ArkadeSwapIntentMonitoringService failed to start"); }

        // Poll boarding UTXOs from the chain. Non-fatal if explorer is unavailable.
        try
        {
            logger.LogInformation("Starting BoardingUtxoPollService...");
            var boardingPoll = services.GetRequiredService<BoardingUtxoPollService>();
            await boardingPoll.StartAsync(cts.Token);
            logger.LogInformation("BoardingUtxoPollService started successfully");
        }
        catch (Exception ex) { logger.LogError(ex, "BoardingUtxoPollService failed to start"); }
    }
}
