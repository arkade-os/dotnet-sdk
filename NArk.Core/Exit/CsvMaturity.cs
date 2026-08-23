using NArk.Abstractions.Blockchain;
using NBitcoin;

namespace NArk.Core.Exit;

/// <summary>
/// Result of a BIP-68 relative-timelock maturity check.
/// </summary>
/// <param name="IsMatured">
/// True once the relative lock on the exit input has elapsed and the claim
/// transaction can be accepted by the network.
/// </param>
/// <param name="Progress">
/// Human-readable "where we are vs where we need to be", for logs. Blocks for
/// a height-based lock, median-time-past timestamps for a time-based one.
/// </param>
public readonly record struct CsvMaturityResult(bool IsMatured, string Progress);

/// <summary>
/// Evaluates BIP-68 relative timelocks (the CSV delay on an Arkade contract's
/// unilateral-exit path) against live chain state.
/// <para>
/// An <see cref="Sequence"/> is <i>not</i> a block count. NBitcoin encodes a
/// time-based relative lock by setting <c>SEQUENCE_LOCKTIME_TYPE_FLAG</c>
/// (bit 22) and storing the delay in 512-second units, so
/// <see cref="Sequence.Value"/> for a 24-hour delay is 4,194,472 — adding that
/// raw number to a block height pushes maturity roughly 80 years out. Always
/// branch on <see cref="Sequence.LockType"/>, which this helper does:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="SequenceLockType.Height"/> — compare the tip
/// height against the input's confirmation height plus
/// <see cref="Sequence.LockHeight"/> blocks.</description></item>
/// <item><description><see cref="SequenceLockType.Time"/> — compare the tip's
/// median time past (BIP 113) against the MTP of the block that confirmed the
/// input plus <see cref="Sequence.LockPeriod"/>. Heights don't enter into it.</description></item>
/// </list>
/// </summary>
public static class CsvMaturity
{
    /// <summary>
    /// Checks whether the relative timelock <paramref name="exitDelay"/> on an
    /// input confirmed at <paramref name="confirmHeight"/> has matured.
    /// </summary>
    /// <param name="exitDelay">The contract's relative-locktime sequence
    /// (typically <c>ArkServerInfo.UnilateralExit</c>).</param>
    /// <param name="confirmHeight">Height of the block that confirmed the input
    /// the claim transaction spends — where the countdown starts.</param>
    /// <param name="blockchain">Backend used to read the tip and, for
    /// time-based locks, the confirmation block's median time past.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="exitDelay"/> is not a relative lock at all, or the
    /// backend could not resolve the confirmation block's median time past for
    /// a time-based lock (without it the timelock cannot be evaluated, and
    /// silently reporting "not matured" would strand the exit forever).
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The lock is time-based and <paramref name="blockchain"/> does not
    /// implement <see cref="IBitcoinBlockchain.GetMedianTimePastAsync"/>.
    /// </exception>
    public static async Task<CsvMaturityResult> EvaluateAsync(
        Sequence exitDelay,
        uint confirmHeight,
        IBitcoinBlockchain blockchain,
        CancellationToken cancellationToken = default)
    {
        if (!exitDelay.IsRelativeLock)
            throw new InvalidOperationException(
                $"Exit delay nSequence {exitDelay.Value} has the disable flag set and is not a " +
                "relative timelock; the Arkade server's unilateral-exit delay is unusable.");

        var chainTime = await blockchain.GetChainTime(cancellationToken);

        if (exitDelay.LockType == SequenceLockType.Height)
        {
            var matureAtHeight = confirmHeight + (uint)exitDelay.LockHeight;
            return new CsvMaturityResult(
                chainTime.Height >= matureAtHeight,
                $"height {chainTime.Height}/{matureAtHeight} ({exitDelay.LockHeight} blocks)");
        }

        // Time-based (BIP 68 bit 22). Consensus compares median-time-past to
        // median-time-past — the confirmation *height* is only a lookup key
        // for the block whose MTP starts the countdown.
        var confirmMtp = await blockchain.GetMedianTimePastAsync(confirmHeight, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Could not resolve median time past for block {confirmHeight}; the time-based " +
                $"unilateral-exit delay ({exitDelay.LockPeriod}) cannot be evaluated. " +
                "Check that the configured blockchain backend is synced past that height.");

        var matureAtTime = confirmMtp + exitDelay.LockPeriod;
        return new CsvMaturityResult(
            chainTime.Timestamp >= matureAtTime,
            $"MTP {chainTime.Timestamp:u}/{matureAtTime:u} ({exitDelay.LockPeriod})");
    }
}
