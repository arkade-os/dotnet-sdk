using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NArk.Core.Models.Options;

namespace NArk.Core.Services;

/// <summary>
/// Background service that periodically runs the exit watchtower check.
/// Register as IHostedService for autonomous monitoring.
/// </summary>
public class ExitWatchtowerBackgroundService(
    ExitWatchtowerService watchtower,
    UnilateralExitService exitService,
    IOptions<ExitWatchtowerOptions> options,
    ILogger<ExitWatchtowerBackgroundService>? logger = null)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.PollInterval;
        logger?.LogInformation("Exit watchtower started, polling every {Interval}", interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Check for partial broadcasts
                await watchtower.CheckAndRespondAsync(stoppingToken);

                // Progress any active exit sessions
                await exitService.ProgressExitsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error in exit watchtower loop");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger?.LogInformation("Exit watchtower stopped");
    }
}
