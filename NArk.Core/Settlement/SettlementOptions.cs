namespace NArk.Core.Settlement;

/// <summary>Tuning knobs for <see cref="SettlementService"/>.</summary>
public class SettlementOptions
{
    /// <summary>
    /// How long to wait after a wallet is queued before evaluating it, so a burst of
    /// VTXO and intent changes collapses into a single evaluation. Defaults to 250 ms.
    /// </summary>
    public TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How often every configured wallet is re-queued. This is the retry behind the
    /// event-driven path: a settlement that failed transiently fires again on the next
    /// beat without any per-wallet resume bookkeeping. Set to
    /// <see cref="TimeSpan.Zero"/> to disable. Defaults to 15 minutes.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Let <see cref="DestinationSweepSettlementService"/> settle to on-chain Bitcoin
    /// addresses via a collaborative exit. Off by default, so an application that
    /// settles Bitcoin its own way — a swap, an exchange withdrawal — can register that
    /// rail without competing with the built-in one for the same destination.
    /// </summary>
    public bool EnableCollaborativeExit { get; set; }
}
