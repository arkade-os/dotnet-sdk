using NArk.ArkadeIntents.Onchain;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Onchain;

namespace NArk.Tests.ArkadeIntents.Onchain;

/// <summary>
/// The checks that stand between a quote and a funded off-board.
/// </summary>
/// <remarks>
/// Two deadlines on two chains govern one swap here. Everything else in this corridor is a contract
/// either side can verify alone; the ordering of those two is the only property neither contract
/// enforces and both parties depend on.
/// </remarks>
[TestFixture]
public class OnchainSendGatesTests
{
    private const long Now = 1_800_000_000;

    [Test]
    public void AWellOrderedQuote_IsFundable()
    {
        Assert.DoesNotThrow(() => OnchainSendGates.AssertFundable(Quote(), Now));
    }

    [Test]
    public void AnExpiredQuote_IsRefused()
    {
        Assert.That(Refusal(Quote(validUntil: Now), Now), Is.EqualTo(OnchainSendRefusalReason.Expired));
    }

    [Test]
    public void TooLittleHeadroomBeforeTheArkadeRefund_IsRefused()
    {
        var quote = Quote(arkadeRefund: Now + 60 * 60, htlcLocktime: Now + 30 * 60);

        Assert.That(Refusal(quote, Now), Is.EqualTo(OnchainSendRefusalReason.InsufficientHeadroom));
    }

    [TestCase(0)]
    [TestCase(7)]
    public void AConfirmationCountOutsideTheRange_IsRefused(int confirmations)
    {
        // A large number turns the claim window into a wait the locktime may not outlast; zero would
        // have us act on a funding that is not on the chain yet.
        Assert.That(Refusal(Quote(minConfirmations: confirmations), Now),
            Is.EqualTo(OnchainSendRefusalReason.ConfirmationsOutOfRange));
    }

    [Test]
    public void AnL1LocktimeLeavingNoRoomToConfirmAndClaim_IsRefused()
    {
        // 6 confirmations is an hour of nominal blocks; with the 90-minute claim margin the locktime
        // must be over two and a half hours out, and this one is two.
        var quote = Quote(
            minConfirmations: 6,
            htlcLocktime: Now + 2 * 60 * 60,
            arkadeRefund: Now + 12 * 60 * 60);

        Assert.That(Refusal(quote, Now), Is.EqualTo(OnchainSendRefusalReason.ClaimWindowTooShort));
    }

    [Test]
    public void TheArkadeRefundOpeningBeforeTheL1One_IsRefused()
    {
        // The reversal that costs both legs: we could reclaim on Arkade while the solver could still
        // reclaim on L1.
        var quote = Quote(htlcLocktime: Now + 10 * 60 * 60, arkadeRefund: Now + 6 * 60 * 60);

        Assert.That(Refusal(quote, Now), Is.EqualTo(OnchainSendRefusalReason.TimelocksOutOfOrder));
    }

    [Test]
    public void TheTwoRefundsTooCloseTogether_IsRefused()
    {
        // Ordered correctly but by an hour, under the two-hour reorg margin the ordering needs.
        var quote = Quote(htlcLocktime: Now + 6 * 60 * 60, arkadeRefund: Now + 7 * 60 * 60);

        Assert.That(Refusal(quote, Now), Is.EqualTo(OnchainSendRefusalReason.TimelocksOutOfOrder));
    }

    [Test]
    public void AQuoteMissingTheL1Deadline_IsRefused()
    {
        var quote = Quote();
        var stripped = new RfqQuote<OnchainSendQuoteProfile>
        {
            RfqId = quote.RfqId, Pair = quote.Pair, FromAmount = quote.FromAmount,
            ToAmount = quote.ToAmount, SolverPubkey = quote.SolverPubkey,
            ValidUntil = quote.ValidUntil, RefundLocktime = quote.RefundLocktime,
            Profile = new OnchainSendQuoteProfile { MinConfirmations = 1 },
        };

        Assert.That(Refusal(stripped, Now), Is.EqualTo(OnchainSendRefusalReason.IncompleteQuote));
    }

    [Test]
    public void TheClaimWindowClosesBeforeTheCounterpartysRefundOpens()
    {
        // Not at the locktime — 90 minutes before it. A claim broadcast inside that margin can fail
        // to confirm before the refund does, which loses the L1 sats and hands over the preimage
        // that takes the Arkade side as well.
        Assert.Multiple(() =>
        {
            Assert.That(OnchainSendGates.ClaimWindowIsOpen(Now + 91 * 60, Now), Is.True);
            Assert.That(OnchainSendGates.ClaimWindowIsOpen(Now + 90 * 60, Now), Is.True, "the margin itself is still open");
            Assert.That(OnchainSendGates.ClaimWindowIsOpen(Now + 89 * 60, Now), Is.False);
            Assert.That(OnchainSendGates.ClaimWindowIsOpen(Now - 1, Now), Is.False, "past the locktime");
        });
    }

    private static OnchainSendRefusalReason Refusal(RfqQuote<OnchainSendQuoteProfile> quote, long now) =>
        Assert.Throws<OnchainSendNotFundableException>(() => OnchainSendGates.AssertFundable(quote, now))!.Reason;

    private static RfqQuote<OnchainSendQuoteProfile> Quote(
        long validUntil = Now + 600,
        long arkadeRefund = Now + 12 * 60 * 60,
        long htlcLocktime = Now + 6 * 60 * 60,
        int minConfirmations = 1) => new()
    {
        RfqId = new string('9', 64),
        Pair = OnchainSendProfile.Pair,
        FromAmount = 50_000,
        ToAmount = 49_850,
        SolverPubkey = new string('e', 64),
        ValidUntil = validUntil,
        RefundLocktime = arkadeRefund,
        Profile = new OnchainSendQuoteProfile
        {
            HtlcLocktime = htlcLocktime,
            MinConfirmations = minConfirmations,
            HtlcAddress = "bcrt1p26p3wqnnngyms2s3zk8dw5xmtf2l4gpu7jh6qdr2xj3uts6m9q8qqae7nc",
            HtlcPubkey = new string('a', 64),
            LockupAddress = "ark1lockup",
            ReceiverPkScript = "5120" + new string('b', 64),
        },
    };
}
