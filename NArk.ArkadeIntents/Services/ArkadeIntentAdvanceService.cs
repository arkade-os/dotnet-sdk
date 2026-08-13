using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NArk.ArkadeIntents.Models;

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
    IArkadeIntentStorage intentStorage,
    IOptions<ArkadeIntentAdvanceOptions>? options = null,
    ILogger<ArkadeIntentAdvanceService>? logger = null) : BackgroundService
{
    private readonly ArkadeIntentAdvanceOptions _options = options?.Value ?? new ArkadeIntentAdvanceOptions();

    /// <summary>
    /// Acts on a swap the moment its status says to, without waiting for the next pass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The monitor is already event-driven: a covenant VTXO changes, and a swap becomes Claimable
    /// within a round trip. Leaving the acting to a timer put a sleep between knowing and doing, for
    /// no reason other than that nothing was listening.
    /// </para>
    /// <para>
    /// On the receive corridor the delay is worse than it looks. A funded lockup sits at an address
    /// no seed can rediscover — nothing of ours ever touched it — while the claim moves those sats
    /// onto our own derivation chain, where a restore would find them. So claiming promptly is not
    /// only about the window closing; it is how funds stop being unrecoverable.
    /// </para>
    /// <para>
    /// Failures are swallowed after logging. This runs on someone else's event, and a storage
    /// notification is no place to throw from.
    /// </para>
    /// </remarks>
    private async void OnSwapChanged(object? sender, ArkadeSwapIntent swap)
    {
        if (ArkadeIntentPolicy.NextAction(swap) is ArkadeIntentAction.None) return;

        try
        {
            var advance = await intents.AdvanceAsync(swap.Id, _stopping);
            if (advance.Action is not ArkadeIntentAction.None)
            {
                logger?.LogInformation(
                    "{Action} on {SwapId}: {Outcome}",
                    advance.Action, advance.SwapId,
                    advance.Acted ? "done" : advance.Error ?? "not yet");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // The timer pass will try again; one swap must not take the notification path down.
            logger?.LogWarning(ex, "acting on swap {SwapId} failed", swap.Id);
        }
    }

    private CancellationToken _stopping;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger?.LogInformation(
                "Swap advance loop disabled; claims and refunds are the caller's to make");
            return;
        }

        _stopping = stoppingToken;
        intentStorage.SwapsChanged += OnSwapChanged;

        try
        {
            await RunPassesAsync(stoppingToken);
        }
        finally
        {
            intentStorage.SwapsChanged -= OnSwapChanged;
        }
    }

    /// <summary>
    /// The periodic sweep behind the event.
    /// </summary>
    /// <remarks>
    /// Still needed, and not merely as insurance. A refund becoming due has no chain event behind
    /// it — nothing happens on-chain when a locktime passes — so a deadline can only ever be noticed
    /// by looking. The event covers what the chain announces; this covers what it does not, and
    /// picks up anything a restart or a failed notification left behind.
    /// </remarks>
    private async Task RunPassesAsync(CancellationToken stoppingToken)
    {
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
