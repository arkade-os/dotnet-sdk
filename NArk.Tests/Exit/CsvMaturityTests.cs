using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Exit;
using NArk.Core.Exit;
using NArk.Core.Transport.Extensions;
using NBitcoin;
using NSubstitute;

namespace NArk.Tests.Exit;

/// <summary>
/// Regression coverage for the BIP-68 relative-timelock maturity check behind
/// unilateral exit.
///
/// The bug this guards: a time-based unilateral-exit delay encodes 512-second
/// units with SEQUENCE_LOCKTIME_TYPE_FLAG (bit 22) set, so its raw nSequence
/// for arkd's production default of 24 h is 4,194,472. Treating that as a block
/// count put maturity ~80 years of blocks away — the exit never matured and no
/// exception was ever thrown. Regtest hid it because
/// ARKD_UNILATERAL_EXIT_DELAY=5 takes the block-based branch.
/// </summary>
[TestFixture]
public class CsvMaturityTests
{
    private const uint ConfirmHeight = 800_000;
    private static readonly DateTimeOffset ConfirmMtp =
        DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private static IBitcoinBlockchain Chain(uint tipHeight, DateTimeOffset tipMtp, DateTimeOffset? confirmMtp = null)
    {
        var blockchain = Substitute.For<IBitcoinBlockchain>();
        blockchain.GetChainTime(Arg.Any<CancellationToken>())
            .Returns(new TimeHeight(tipMtp, tipHeight));
        blockchain.GetMedianTimePastAsync(ConfirmHeight, Arg.Any<CancellationToken>())
            .Returns(confirmMtp);
        return blockchain;
    }

    // ── arkd's delay encoding ──────────────────────────────────────

    [Test]
    public void ExitDelayEncoding_BelowFiveTwelve_IsBlockBased()
    {
        var sequence = 5L.ToExitDelaySequence();

        Assert.That(sequence.LockType, Is.EqualTo(SequenceLockType.Height));
        Assert.That(sequence.LockHeight, Is.EqualTo(5));
        Assert.That(sequence.Value, Is.EqualTo(5u));
    }

    [Test]
    public void ExitDelayEncoding_ArkdProductionDefault_IsTimeBasedWithFlaggedValue()
    {
        // arkd's defaultUnilateralExitDelay is 86400 (24 h).
        var sequence = 86_400L.ToExitDelaySequence();

        Assert.That(sequence.LockType, Is.EqualTo(SequenceLockType.Time));
        // BIP 68's 512-second granularity truncates: 86400 / 512 = 168 units
        // = 86016 s, so the encoded lock is marginally shorter than configured.
        Assert.That(sequence.LockPeriod, Is.EqualTo(TimeSpan.FromSeconds(86_016)));
        // The raw value carries bit 22 — this is the number that used to be
        // added to a block height.
        Assert.That(sequence.Value, Is.EqualTo(4_194_472u));
    }

    // ── Height-based locks ─────────────────────────────────────────

    [Test]
    public async Task HeightLock_NotMaturedBeforeDelayElapses()
    {
        var delay = new Sequence(144);
        var chain = Chain(tipHeight: ConfirmHeight + 143, tipMtp: ConfirmMtp);

        var result = await CsvMaturity.EvaluateAsync(delay, ConfirmHeight, chain);

        Assert.That(result.IsMatured, Is.False);
    }

    [Test]
    public async Task HeightLock_MaturedOnceDelayElapses()
    {
        var delay = new Sequence(144);
        var chain = Chain(tipHeight: ConfirmHeight + 144, tipMtp: ConfirmMtp);

        var result = await CsvMaturity.EvaluateAsync(delay, ConfirmHeight, chain);

        Assert.That(result.IsMatured, Is.True);
    }

    [Test]
    public async Task HeightLock_DoesNotConsultMedianTimePast()
    {
        var chain = Chain(tipHeight: ConfirmHeight + 144, tipMtp: ConfirmMtp);

        await CsvMaturity.EvaluateAsync(new Sequence(144), ConfirmHeight, chain);

        await chain.DidNotReceive().GetMedianTimePastAsync(Arg.Any<uint>(), Arg.Any<CancellationToken>());
    }

    // ── Time-based locks ───────────────────────────────────────────

    [Test]
    public async Task TimeLock_NotMaturedBeforePeriodElapses()
    {
        var delay = 86_400L.ToExitDelaySequence();
        var chain = Chain(
            tipHeight: ConfirmHeight + 200,
            tipMtp: ConfirmMtp.AddHours(23),
            confirmMtp: ConfirmMtp);

        var result = await CsvMaturity.EvaluateAsync(delay, ConfirmHeight, chain);

        Assert.That(result.IsMatured, Is.False);
    }

    [Test]
    public async Task TimeLock_MaturedOncePeriodElapses()
    {
        var delay = 86_400L.ToExitDelaySequence();
        var chain = Chain(
            tipHeight: ConfirmHeight + 200,
            tipMtp: ConfirmMtp.AddHours(24),
            confirmMtp: ConfirmMtp);

        var result = await CsvMaturity.EvaluateAsync(delay, ConfirmHeight, chain);

        Assert.That(result.IsMatured, Is.True);
    }

    /// <summary>
    /// The core regression: a time-based delay must NOT be evaluated against
    /// block heights. Under the old arithmetic, a chain 200 blocks past the
    /// leaf with the full 24 h elapsed still reported "not matured" — and would
    /// have kept doing so for ~80 years of blocks.
    /// </summary>
    [Test]
    public async Task TimeLock_MaturesOnMedianTimePast_NotBlockHeight()
    {
        var delay = 86_400L.ToExitDelaySequence();
        // Only 6 blocks past the leaf, but a full day of median-time-past has
        // elapsed — which is exactly what BIP 68 consensus checks.
        var chain = Chain(
            tipHeight: ConfirmHeight + 6,
            tipMtp: ConfirmMtp.AddHours(25),
            confirmMtp: ConfirmMtp);

        var result = await CsvMaturity.EvaluateAsync(delay, ConfirmHeight, chain);

        Assert.That(result.IsMatured, Is.True,
            "Time-based locks mature on MTP; block height is irrelevant to them");
    }

    [Test]
    public void TimeLock_ThrowsWhenMedianTimePastUnavailable()
    {
        var delay = 86_400L.ToExitDelaySequence();
        var chain = Chain(
            tipHeight: ConfirmHeight + 200,
            tipMtp: ConfirmMtp.AddHours(48),
            confirmMtp: null);

        // Loud failure beats a silent "never matures".
        Assert.ThrowsAsync<InvalidOperationException>(
            () => CsvMaturity.EvaluateAsync(delay, ConfirmHeight, chain));
    }

    [Test]
    public void TimeLock_ThrowsWhenBackendCannotResolveMedianTimePast()
    {
        var delay = 86_400L.ToExitDelaySequence();
        var chain = Substitute.For<IBitcoinBlockchain>();
        chain.GetChainTime(Arg.Any<CancellationToken>())
            .Returns(new TimeHeight(ConfirmMtp.AddHours(48), ConfirmHeight + 200));
        chain.GetMedianTimePastAsync(Arg.Any<uint>(), Arg.Any<CancellationToken>())
            .Returns<Task<DateTimeOffset?>>(_ => throw new NotSupportedException("no MTP here"));

        Assert.ThrowsAsync<NotSupportedException>(
            () => CsvMaturity.EvaluateAsync(delay, ConfirmHeight, chain));
    }

    [Test]
    public void NonRelativeSequence_ThrowsRatherThanWaitingForever()
    {
        var chain = Chain(tipHeight: ConfirmHeight + 1000, tipMtp: ConfirmMtp);

        Assert.ThrowsAsync<InvalidOperationException>(
            () => CsvMaturity.EvaluateAsync(Sequence.Final, ConfirmHeight, chain));
    }

    // ── ExitPlan delay round-trip ──────────────────────────────────

    private static ExitPlan Plan(int csvDelay, uint exitSequence) => new(
        WalletId: "w1",
        VtxoTxid: uint256.One.ToString(),
        VtxoVout: 0,
        ClaimAddress: "bcrt1qexample",
        LeafTxid: uint256.One.ToString(),
        CsvDelay: csvDelay,
        ExitSequence: exitSequence);

    [Test]
    public void ExitPlan_RoundTripsTimeBasedDelay()
    {
        var delay = 86_400L.ToExitDelaySequence();
        var plan = Plan(csvDelay: 0, exitSequence: delay.Value);

        Assert.That(plan.ExitDelay.LockType, Is.EqualTo(SequenceLockType.Time));
        Assert.That(plan.ExitDelay.LockPeriod, Is.EqualTo(TimeSpan.FromSeconds(86_016)));
    }

    [Test]
    public void ExitPlan_RoundTripsHeightBasedDelay()
    {
        var delay = 5L.ToExitDelaySequence();
        var plan = Plan(csvDelay: delay.LockHeight, exitSequence: delay.Value);

        Assert.That(plan.ExitDelay.LockType, Is.EqualTo(SequenceLockType.Height));
        Assert.That(plan.ExitDelay.LockHeight, Is.EqualTo(5));
    }

    [Test]
    public void ExitPlan_LegacyBlockOnlyPlan_StillResolves()
    {
        // Plans persisted before ExitSequence existed carry only CsvDelay, and
        // were only ever correct for block-based delays.
        var plan = Plan(csvDelay: 5, exitSequence: 0);

        Assert.That(plan.ExitDelay.LockType, Is.EqualTo(SequenceLockType.Height));
        Assert.That(plan.ExitDelay.LockHeight, Is.EqualTo(5));
    }

    [Test]
    public void ExitPlan_LegacyPlanWithRawTimeSequenceInCsvDelay_ThrowsLoudly()
    {
        // What an older SDK recorded against a 24 h server: the raw nSequence
        // stored as if it were a block count.
        var plan = Plan(csvDelay: 4_194_472, exitSequence: 0);

        Assert.Throws<InvalidOperationException>(() => _ = plan.ExitDelay);
    }
}
