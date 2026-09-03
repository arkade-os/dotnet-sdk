using Microsoft.Extensions.Logging;
using NArk.Core.Services;

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

        // Start Arkade asset-swap monitoring (transitions covenant swaps: filled by a solver / cancelled).
        try
        {
            var arkadeSwaps = services.GetRequiredService<NArk.ArkadeIntents.Services.ArkadeSwapIntentMonitoringService>();
            await arkadeSwaps.StartAsync(cts.Token);
        }
        catch (Exception ex) { logger.LogWarning(ex, "ArkadeSwapIntentMonitoringService failed to start"); }

        // The monitor only moves a swap's status. Acting on that status — claiming a funded receive,
        // refunding a send the solver never filled — is this loop, and it is not optional: the claim
        // window is a couple of hours, and a swap that reaches Claimable while nobody is looking at
        // the Swap page is a payment that quietly does not arrive. What to do is decided by
        // ArkadeIntentPolicy; this only supplies a clock.
        try
        {
            var intents = services.GetRequiredService<NArk.ArkadeIntents.Services.ArkadeIntentsService>();
            _ = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
                while (await timer.WaitForNextTickAsync(cts.Token))
                {
                    try
                    {
                        foreach (var advance in await intents.AdvanceAllAsync(cancellationToken: cts.Token))
                        {
                            if (advance.Action is not NArk.ArkadeIntents.Services.ArkadeIntentAction.None)
                            {
                                logger.LogInformation(
                                    "{Action} on {SwapId}: {Outcome}", advance.Action, advance.SwapId,
                                    advance.Acted ? "done" : advance.Error ?? "not yet");
                            }
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    // A pass over many swaps: one that cannot proceed must not stop the others,
                    // and must not end the loop that will retry it in thirty seconds.
                    catch (Exception ex) { logger.LogWarning(ex, "swap advance pass failed"); }
                }
            }, cts.Token);
        }
        catch (Exception ex) { logger.LogWarning(ex, "swap advance loop failed to start"); }

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
