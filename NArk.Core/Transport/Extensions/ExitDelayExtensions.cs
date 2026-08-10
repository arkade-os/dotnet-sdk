using NBitcoin;

namespace NArk.Core.Transport.Extensions;

/// <summary>
/// Decodes the exit-delay fields arkd advertises on <c>GetInfo</c>
/// (<c>unilateral_exit_delay</c>, <c>boarding_exit_delay</c>).
/// </summary>
public static class ExitDelayExtensions
{
    /// <summary>
    /// Converts an arkd exit-delay value into a BIP-68 relative timelock.
    /// arkd overloads a single integer: values below 512 are a block count,
    /// values of 512 or more are seconds (matching BIP 68's 512-second
    /// granularity for time-based locks) — the same convention go-sdk and
    /// ts-sdk apply.
    /// </summary>
    /// <remarks>
    /// The resulting <see cref="Sequence"/> is <b>not</b> interchangeable with
    /// a block count. For a time-based delay, NBitcoin sets
    /// <c>SEQUENCE_LOCKTIME_TYPE_FLAG</c> (bit 22) and stores 512-second units,
    /// so <see cref="Sequence.Value"/> for 24 hours is 4,194,472. Always branch
    /// on <see cref="Sequence.LockType"/> before doing arithmetic with it —
    /// see <c>NArk.Core.Exit.CsvMaturity</c>.
    /// </remarks>
    /// <param name="value">Raw delay as advertised by arkd.</param>
    public static Sequence ToExitDelaySequence(this long value)
        => value >= 512 ? new Sequence(TimeSpan.FromSeconds(value)) : new Sequence((int)value);
}
