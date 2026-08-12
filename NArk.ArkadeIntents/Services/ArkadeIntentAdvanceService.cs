using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NArk.ArkadeIntents.Services;

/// <summary>How often swaps are acted on, and whether they are acted on at all.</summary>
public sealed class ArkadeIntentAdvanceOptions
{
    /// <summary>How long between passes. Defaults to 30 seconds.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Set false to drive claims and refunds by hand instead.
    /// </summary>
    /// <remarks>
    /// Worth being deliberate about: turning this off means something else must claim inside the
    /// receive window, and nothing warns when that turns out to be nobody.
    /// </remarks>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Does for every swap whatever its state already calls for.
/// </summary>
/// <remarks>
/// <para>
/// The companion to <see cref="ArkadeSwapIntentMonitoringService"/>, and registered beside it
/// because half the mechanism is worse than none. The monitor observes — it moves a swap to
/// Claimable when the counterparty's lockup appears, and to Fulfilled or Resolved once it is
/// spent — and observing was all that happened by default. Acting on what it observed was left to
/// whoever wired the SDK up, which is a dangerous thing to leave out: the receive window is a
/// couple of hours, and a swap that reaches Claimable while nobody is running a loop is a payment
/// that quietly does not arrive.
/// </para>
/// <para>
/// What to do is decided by <see cref="ArkadeIntentPolicy"/> and not here; this supplies a clock.
/// The two actions it will take are claiming a funded receive and refunding a send the solver
/// never filled. Cancelling a pending swap is deliberately not among them — a pending swap is
/// waiting to be filled, which is what was asked for.
/// </para>
/// <para>
/// A pass covers many swaps, so a failure is logged and the pass continues: one swap that cannot
/// proceed must not stop the others, nor end the loop that would have retried it.
/// </para>
/// </remarks>
public sealed class ArkadeIntentAdvanceService(
    ArkadeIntentsService intents,
    IOptions<ArkadeIntentAdvanceOptions>? options = null,
    ILogger<ArkadeIntentAdvanceService>? logger = null) : BackgroundService
{
    private readonly ArkadeIntentAdvanceOptions _options = options?.Value ?? new ArkadeIntentAdvanceOptions();

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger?.LogInformation(
                "Swap advance loop disabled; claims and refunds are the caller's to make");
            return;
        }

        using var timer = new PeriodicTimer(_options.Interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var advance in await intents.AdvanceAllAsync(cancellationToken: stoppingToken))
                {
                    if (advance.Action is ArkadeIntentAction.None) continue;

                    logger?.LogInformation(
                        "{Action} on {SwapId}: {Outcome}",
                        advance.Action, advance.SwapId,
                        advance.Acted ? "done" : advance.Error ?? "not yet");
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "swap advance pass failed; retrying in {Interval}", _options.Interval);
            }

            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
