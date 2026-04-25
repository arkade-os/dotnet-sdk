namespace NArk.Abstractions.Sync;

/// <summary>
/// Persistent marker recording the last time the SDK successfully polled
/// every active script in the indexer for VTXO state. Used to bound the
/// catch-up window after a process restart so wallets with long history
/// don't re-fetch their entire VTXO set on every cold start.
/// </summary>
public interface ISyncStateStorage
{
    /// <summary>
    /// Returns the timestamp of the last completed full-set VTXO poll, or
    /// <c>null</c> if no full poll has ever been recorded.
    /// </summary>
    Task<DateTimeOffset?> GetLastFullPollAtAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the timestamp of a just-completed full-set VTXO poll. Should
    /// be called only after a poll that covered the entire active script set
    /// returned without error — partial polls (stream pushes, newly-added
    /// scripts) MUST NOT update this value, otherwise we'd advance the
    /// catch-up cursor past changes we never saw.
    /// </summary>
    /// <param name="value">
    /// Should typically be the wall-clock time the poll <em>started</em>, not
    /// when it finished — using "started" ensures any change that landed on
    /// arkd between start and finish is still inside the next poll's window.
    /// </param>
    Task SetLastFullPollAtAsync(DateTimeOffset value, CancellationToken cancellationToken = default);
}
