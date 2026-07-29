using NArk.Swaps.Evm;
using NUnit.Framework;
using static NArk.Swaps.Evm.EvmSwapTimeoutValidator;

namespace NArk.Tests.Swaps;

/// <summary>
/// Unit tests for <see cref="EvmSwapTimeoutValidator"/> — the cross-chain timelock ordering
/// checked before any funds are committed to a chain swap.
/// </summary>
/// <remarks>
/// The invariant: the leg we claim must expire safely before our own lockup becomes refundable.
/// Reverse that and the swap stops being atomic — there is a window where our funds are already
/// refundable to us while the counterparty's leg is still live, which is exactly the arrangement
/// no honest counterparty will complete.
/// </remarks>
[TestFixture]
public class EvmSwapTimeoutValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan MinClaim = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MinMargin = TimeSpan.FromMinutes(30);

    private static SwapTimeoutViolation Check(TimeSpan claimIn, TimeSpan refundIn) =>
        Validate(Now, Now + claimIn, Now + refundIn, MinClaim, MinMargin);

    // ── Ordering ────────────────────────────────────────────────────────────

    [Test]
    public void WellOrderedTimeouts_Pass() =>
        Assert.That(Check(claimIn: TimeSpan.FromHours(2), refundIn: TimeSpan.FromHours(6)),
            Is.EqualTo(SwapTimeoutViolation.None));

    [Test]
    public void OurRefundBeforeTheirExpiry_IsRejected() =>
        Assert.That(Check(claimIn: TimeSpan.FromHours(6), refundIn: TimeSpan.FromHours(2)),
            Is.EqualTo(SwapTimeoutViolation.RefundNotAfterClaimDeadline));

    [Test]
    public void SimultaneousExpiry_IsRejected() =>
        Assert.That(Check(claimIn: TimeSpan.FromHours(2), refundIn: TimeSpan.FromHours(2)),
            Is.EqualTo(SwapTimeoutViolation.RefundNotAfterClaimDeadline));

    /// <summary>The margin is the whole point — an ordering that is technically correct but too
    /// tight to survive block-time estimation error must still be rejected.</summary>
    [Test]
    public void OrderingCorrectButInsideTheMargin_IsRejected() =>
        Assert.That(Check(claimIn: TimeSpan.FromHours(2), refundIn: TimeSpan.FromHours(2) + TimeSpan.FromMinutes(29)),
            Is.EqualTo(SwapTimeoutViolation.RefundNotAfterClaimDeadline));

    [Test]
    public void OrderingExactlyAtTheMargin_Passes() =>
        Assert.That(Check(claimIn: TimeSpan.FromHours(2), refundIn: TimeSpan.FromHours(2) + MinMargin),
            Is.EqualTo(SwapTimeoutViolation.None));

    // ── Claim window ────────────────────────────────────────────────────────

    [Test]
    public void ClaimDeadlineTooSoon_IsRejected() =>
        Assert.That(Check(claimIn: TimeSpan.FromMinutes(5), refundIn: TimeSpan.FromHours(6)),
            Is.EqualTo(SwapTimeoutViolation.ClaimWindowTooShort));

    [Test]
    public void ClaimDeadlineAlreadyPast_IsRejected() =>
        Assert.That(Check(claimIn: TimeSpan.FromHours(-1), refundIn: TimeSpan.FromHours(6)),
            Is.EqualTo(SwapTimeoutViolation.ClaimWindowTooShort));

    /// <summary>Claim window is checked first: with both broken, the message should name the one
    /// that makes the swap unusable soonest.</summary>
    [Test]
    public void BothViolated_ReportsClaimWindow() =>
        Assert.That(Check(claimIn: TimeSpan.FromMinutes(1), refundIn: TimeSpan.FromMinutes(-60)),
            Is.EqualTo(SwapTimeoutViolation.ClaimWindowTooShort));

    // ── Block → wall clock ──────────────────────────────────────────────────

    [Test]
    public void BlockToDeadline_ConvertsRemainingBlocks()
    {
        var deadline = BlockToDeadline(Now, currentBlock: 1_000, targetBlock: 1_060, TimeSpan.FromMinutes(10));
        Assert.That(deadline, Is.EqualTo(Now + TimeSpan.FromMinutes(600)));
    }

    [TestCase(1_000)]
    [TestCase(999)]
    public void BlockToDeadline_TargetAtOrBelowCurrent_IsAlreadyExpired(long target) =>
        Assert.That(BlockToDeadline(Now, currentBlock: 1_000, targetBlock: target, TimeSpan.FromMinutes(10)),
            Is.EqualTo(Now));

    /// <summary>
    /// An absurd target block (wrong units, a parse error) must not overflow into a deadline so
    /// far out that every check trivially passes — that would silently disable the validation.
    /// </summary>
    [Test]
    public void BlockToDeadline_AbsurdTarget_SaturatesInsteadOfOverflowing()
    {
        var deadline = BlockToDeadline(Now, currentBlock: 0, targetBlock: long.MaxValue, TimeSpan.FromMinutes(10));
        Assert.That(deadline, Is.EqualTo(DateTimeOffset.MaxValue));
    }

    [Test]
    public void Describe_NamesTheOffendingDeadline()
    {
        var claim = Now + TimeSpan.FromMinutes(5);
        var refund = Now + TimeSpan.FromHours(6);

        Assert.That(
            Describe(SwapTimeoutViolation.ClaimWindowTooShort, Now, claim, refund),
            Does.Contain("too little time to claim"));
        Assert.That(
            Describe(SwapTimeoutViolation.RefundNotAfterClaimDeadline, Now, claim, refund),
            Does.Contain("would not be atomic"));
    }
}
