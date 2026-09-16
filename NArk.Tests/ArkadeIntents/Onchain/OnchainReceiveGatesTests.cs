using NArk.ArkadeIntents.Onchain;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Onchain;

namespace NArk.Tests.ArkadeIntents.Onchain;

/// <summary>
/// The checks that stand between a quote and a funded on-board.
/// </summary>
/// <remarks>
/// The same two-deadlines-on-two-chains problem as the off-board, arranged the other way round.
/// There we funded Arkade and our refund had to open last; here we fund L1 and the solver's Arkade
/// refund has to open first, because on this leg it is the solver paying out ahead of being paid.
/// Getting that order backwards is the one failure that can cost both legs at once, and neither
/// contract enforces it.
/// </remarks>
[TestFixture]
public class OnchainReceiveGatesTests
{
    private const long Now = 1_800_000_000;

    [Test]
    public void AWellOrderedQuote_IsFundable()
    {
        Assert.DoesNotThrow(() => OnchainReceiveGates.AssertFundable(Quote(), Now));
    }

    [Test]
    public void AnExpiredQuote_IsRefused()
    {
        Assert.That(Refusal(Quote(validUntil: Now), Now), Is.EqualTo(OnchainReceiveRefusalReason.Expired));
    }

    [Test]
    public void TooLittleHeadroomBeforeTheSolversArkadeReclaim_IsRefused()
    {
        // On this leg the solver's refund_locktime is OUR claim deadline, so the send leg's headroom
        // rule lands on it rather than on a deadline of our own.
        var quote = Quote(arkadeRefund: Now + 60 * 60, htlcLocktime: Now + 8 * 60 * 60);

        Assert.That(Refusal(quote, Now), Is.EqualTo(OnchainReceiveRefusalReason.InsufficientHeadroom));
    }

    [TestCase(0)]
    [TestCase(7)]
    public void AConfirmationCountOutsideTheRange_IsRefused(int confirmations)
    {
        // Zero would have the solver act on a funding that can still be replaced; a large number
        // stretches the wait past what the L1 deadline can hold.
        Assert.That(Refusal(Quote(minConfirmations: confirmations), Now),
            Is.EqualTo(OnchainReceiveRefusalReason.ConfirmationsOutOfRange));
    }

    [Test]
    public void AnL1LocktimeLeavingNoRoomToConfirmAndSettle_IsRefused()
    {
        // 6 confirmations is an hour of nominal blocks, and the claim margin is 90 minutes, so the
        // L1 deadline has to be over two and a half hours out. This one is two — and a solver that
        // cannot settle inside it will not fund the Arkade side at all, leaving our sats parked in
        // an HTLC until we take them back.
        var quote = Quote(
            minConfirmations: 6,
            htlcLocktime: Now + 2 * 60 * 60,
            arkadeRefund: Now + 100 * 60);

        Assert.That(Refusal(quote, Now), Is.EqualTo(OnchainReceiveRefusalReason.ClaimWindowTooShort));
    }

    [Test]
    public void TheL1RefundOpeningBeforeTheArkadeOne_IsRefused()
    {
        // The reversal, mirrored: here it is OUR L1 refund opening first that creates the window in
        // which one leg pays for both — we could take the L1 sats back while still holding a
        // claimable Arkade lockup. A swap only completable by robbing the counterparty is one no
        // honest solver offered, so it is refused rather than taken.
        var quote = Quote(htlcLocktime: Now + 6 * 60 * 60, arkadeRefund: Now + 10 * 60 * 60);

        Assert.That(Refusal(quote, Now), Is.EqualTo(OnchainReceiveRefusalReason.TimelocksOutOfOrder));
    }

    [Test]
    public void TheTwoRefundsTooCloseTogether_IsRefused()
    {
        // Ordered correctly, but by five minutes rather than the fifteen the settle margin needs.
        var quote = Quote(arkadeRefund: Now + 6 * 60 * 60, htlcLocktime: Now + 6 * 60 * 60 + 5 * 60);

        Assert.That(Refusal(quote, Now), Is.EqualTo(OnchainReceiveRefusalReason.TimelocksOutOfOrder));
    }

    [Test]
    public void TheOrderingTheReferenceSolverProduces_IsAccepted()
    {
        // The solver sizes its own Arkade refund as htlc_locktime minus the settle margin, capped.
        // Checking the ordering must not refuse the very quote that construction yields, or the
        // corridor would refuse every honest counterparty.
        var htlcLocktime = Now + 8 * 60 * 60;
        var quote = Quote(htlcLocktime: htlcLocktime, arkadeRefund: htlcLocktime - 15 * 60);

        Assert.DoesNotThrow(() => OnchainReceiveGates.AssertFundable(quote, Now));
    }

    [Test]
    public void AQuoteMissingTheL1Deadline_IsRefused()
    {
        var quote = Quote();
        var stripped = new RfqQuote<OnchainReceiveQuoteProfile>
        {
            RfqId = quote.RfqId, Pair = quote.Pair, FromAmount = quote.FromAmount,
            ToAmount = quote.ToAmount, SolverPubkey = quote.SolverPubkey,
            ValidUntil = quote.ValidUntil, RefundLocktime = quote.RefundLocktime,
            Profile = new OnchainReceiveQuoteProfile { MinConfirmations = 1 },
        };

        Assert.That(Refusal(stripped, Now), Is.EqualTo(OnchainReceiveRefusalReason.IncompleteQuote));
    }

    [Test]
    public void AQuoteMissingTheConfirmationCount_IsRefused()
    {
        var quote = Quote();
        var stripped = new RfqQuote<OnchainReceiveQuoteProfile>
        {
            RfqId = quote.RfqId, Pair = quote.Pair, FromAmount = quote.FromAmount,
            ToAmount = quote.ToAmount, SolverPubkey = quote.SolverPubkey,
            ValidUntil = quote.ValidUntil, RefundLocktime = quote.RefundLocktime,
            Profile = new OnchainReceiveQuoteProfile { HtlcLocktime = Now + 8 * 60 * 60 },
        };

        Assert.That(Refusal(stripped, Now), Is.EqualTo(OnchainReceiveRefusalReason.IncompleteQuote));
    }

    [Test]
    public void TheClaimWindowClosesBeforeTheSolversReclaimOpens()
    {
        // Not at the locktime — 90 minutes before it. A claim broadcast inside that margin can fail
        // to confirm before the solver's reclaim does, which loses the Arkade payout AND publishes
        // the preimage that takes our L1 funding as well.
        Assert.Multiple(() =>
        {
            Assert.That(OnchainReceiveGates.ClaimWindowIsOpen(Now + 91 * 60, Now), Is.True);
            Assert.That(OnchainReceiveGates.ClaimWindowIsOpen(Now + 90 * 60, Now), Is.True,
                "the margin itself is still open");
            Assert.That(OnchainReceiveGates.ClaimWindowIsOpen(Now + 89 * 60, Now), Is.False);
            Assert.That(OnchainReceiveGates.ClaimWindowIsOpen(Now - 1, Now), Is.False, "past the locktime");
        });
    }

    [Test]
    public void TheRefundIsDueAgainstMedianTimePast_NotTheWallClock()
    {
        // Consensus matures CLTV against median time past, which trails wall clock by about an hour.
        // A refund built when only the local clock says so is well formed and rejected as non-final,
        // and the rejection says nothing about which clock was wrong.
        Assert.Multiple(() =>
        {
            Assert.That(OnchainReceiveGates.RefundIsDue(Now, Now - 1), Is.False, "a second short");
            Assert.That(OnchainReceiveGates.RefundIsDue(Now, Now), Is.True, "exactly due");
            Assert.That(OnchainReceiveGates.RefundIsDue(Now, Now + 1), Is.True);
        });
    }

    [Test]
    public void TheSharedNumbers_AreBoundToTheSendLeg_NotCopied()
    {
        // Two constants that must agree and are written twice are two constants that will eventually
        // disagree, and the symptom is a corridor funding swaps its own mirror image refuses.
        Assert.Multiple(() =>
        {
            Assert.That(OnchainReceiveGates.MaxMinConfirmations, Is.EqualTo(OnchainSendGates.MaxMinConfirmations));
            Assert.That(OnchainReceiveGates.SecondsPerBlock, Is.EqualTo(OnchainSendGates.SecondsPerBlock));
            Assert.That(OnchainReceiveGates.ClaimMarginSeconds, Is.EqualTo(OnchainSendGates.ClaimMarginSeconds));
            Assert.That(OnchainReceiveGates.MinHeadroomSeconds, Is.EqualTo(OnchainSendGates.MinHeadroomSeconds));
        });
    }

    private static OnchainReceiveRefusalReason Refusal(RfqQuote<OnchainReceiveQuoteProfile> quote, long now) =>
        Assert.Throws<OnchainReceiveNotFundableException>(
            () => OnchainReceiveGates.AssertFundable(quote, now))!.Reason;

    private static RfqQuote<OnchainReceiveQuoteProfile> Quote(
        long validUntil = Now + 600,
        long arkadeRefund = Now + 6 * 60 * 60,
        long htlcLocktime = Now + 8 * 60 * 60,
        int minConfirmations = 1) => new()
    {
        RfqId = new string('9', 64),
        Pair = OnchainReceiveProfile.Pair,
        FromAmount = 50_000,
        ToAmount = 49_850,
        SolverPubkey = new string('e', 64),
        ValidUntil = validUntil,
        RefundLocktime = arkadeRefund,
        Profile = new OnchainReceiveQuoteProfile
        {
            HtlcLocktime = htlcLocktime,
            MinConfirmations = minConfirmations,
            HtlcAddress = "bcrt1p26p3wqnnngyms2s3zk8dw5xmtf2l4gpu7jh6qdr2xj3uts6m9q8qqae7nc",
            ClaimPubkey = new string('a', 64),
            LockupAddress = "ark1lockup",
            SolverRefundPkScript = "5120" + new string('b', 64),
        },
    };
}
