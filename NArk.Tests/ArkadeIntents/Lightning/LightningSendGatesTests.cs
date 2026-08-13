using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Lightning;

namespace NArk.Tests.ArkadeIntents.Lightning;

/// <summary>
/// The maker's funding gates, checked at their exact boundaries.
/// </summary>
/// <remarks>
/// Each of these guards an irreversible step: past them the sats are locked in a contract only the
/// solver can claim, and the only way out is the refund path opening. They are pure functions over
/// an injected clock precisely so the boundary can be asserted to the second rather than inferred.
/// </remarks>
[TestFixture]
public class LightningSendGatesTests
{
    private const long Now = 1_800_000_000;
    private const long InvoiceAmount = 50_000;

    [Test]
    public void Fundable_WhenEveryPreconditionHolds()
    {
        Assert.DoesNotThrow(() => LightningSendGates.AssertFundable(
            Quote(), InvoiceAmount, Now + 3600, Now));
    }

    [Test]
    public void Headroom_OfExactlyNinetyMinutes_IsEnough()
    {
        var quote = Quote(refundLocktime: Now + LightningSendGates.MinHeadroomSeconds);

        Assert.DoesNotThrow(() => LightningSendGates.AssertFundable(quote, InvoiceAmount, Now + 3600, Now));
    }

    [Test]
    public void Headroom_OneSecondShort_Refuses()
    {
        // The refund deadline matures against median-time-past, which lags wall clock by about an
        // hour — shaving this margin is not a trade-off, it is removing the margin.
        var quote = Quote(refundLocktime: Now + LightningSendGates.MinHeadroomSeconds - 1);

        var ex = Assert.Throws<LightningSendNotFundableException>(() =>
            LightningSendGates.AssertFundable(quote, InvoiceAmount, Now + 3600, Now));

        Assert.That(ex!.Reason, Is.EqualTo(FundingRefusal.InsufficientHeadroom));
    }

    [Test]
    public void QuoteValidity_IsExclusiveAtTheBoundary()
    {
        var quote = Quote(validUntil: Now);

        var ex = Assert.Throws<LightningSendNotFundableException>(() =>
            LightningSendGates.AssertFundable(quote, InvoiceAmount, Now + 3600, Now));

        Assert.That(ex!.Reason, Is.EqualTo(FundingRefusal.QuoteExpired));
    }

    [Test]
    public void QuoteValidity_OneSecondBefore_StillFunds()
    {
        var quote = Quote(validUntil: Now + 1);

        Assert.DoesNotThrow(() => LightningSendGates.AssertFundable(quote, InvoiceAmount, Now + 3600, Now));
    }

    [Test]
    public void InvoiceExpiry_IsExclusiveAtTheBoundary()
    {
        var ex = Assert.Throws<LightningSendNotFundableException>(() =>
            LightningSendGates.AssertFundable(Quote(), InvoiceAmount, Now, Now));

        Assert.That(ex!.Reason, Is.EqualTo(FundingRefusal.InvoiceExpired));
    }

    [Test]
    public void ADeadInvoice_IsReportedBeforeAStaleQuote()
    {
        // Both are true here; the invoice is the more actionable answer — a fresh quote will not
        // help, the payee must issue a new invoice.
        var quote = Quote(validUntil: Now - 1);

        var ex = Assert.Throws<LightningSendNotFundableException>(() =>
            LightningSendGates.AssertFundable(quote, InvoiceAmount, Now - 1, Now));

        Assert.That(ex!.Reason, Is.EqualTo(FundingRefusal.InvoiceExpired));
    }

    [Test]
    public void AQuoteThatPaysSomethingElse_IsNotAQuoteForThisInvoice()
    {
        // Exact-out: the invoice fixes the payout. A solver quoting a different one has either
        // misread the invoice or is quoting a different swap.
        var quote = Quote(toAmount: InvoiceAmount - 1);

        var ex = Assert.Throws<LightningSendNotFundableException>(() =>
            LightningSendGates.AssertFundable(quote, InvoiceAmount, Now + 3600, Now));

        Assert.That(ex!.Reason, Is.EqualTo(FundingRefusal.AmountMismatch));
    }

    [Test]
    public void ASolverQuotingLessThanItPaysOut_IsRefused()
    {
        var quote = Quote(fromAmount: InvoiceAmount - 1);

        var ex = Assert.Throws<LightningSendNotFundableException>(() =>
            LightningSendGates.AssertFundable(quote, InvoiceAmount, Now + 3600, Now));

        Assert.That(ex!.Reason, Is.EqualTo(FundingRefusal.AmountMismatch));
    }

    [Test]
    public void ASpreadInTheSolversFavour_IsFine()
    {
        // The fee lives in the gap between the amounts; there is no separate fee field.
        var quote = Quote(fromAmount: InvoiceAmount + 150);

        Assert.DoesNotThrow(() => LightningSendGates.AssertFundable(quote, InvoiceAmount, Now + 3600, Now));
    }

    [Test]
    public void VerifyLockupAddress_ReturnsTheAddressWhenItMatches()
    {
        var quote = Quote(lockupAddress: "ark1qlockup");

        Assert.That(LightningSendGates.VerifyLockupAddress(quote, "ark1qlockup"), Is.EqualTo("ark1qlockup"));
    }

    [Test]
    public void VerifyLockupAddress_ThrowsOnAnyDifference()
    {
        var quote = Quote(lockupAddress: "ark1qsomewhere-else");

        var ex = Assert.Throws<LockupAddressMismatchException>(() =>
            LightningSendGates.VerifyLockupAddress(quote, "ark1qlockup"));

        Assert.That(ex!.Derived, Is.EqualTo("ark1qlockup"));
        Assert.That(ex.Quoted, Is.EqualTo("ark1qsomewhere-else"));
    }

    [Test]
    public void VerifyLockupAddress_ThrowsWhenTheSolverSentNoneAtAll()
    {
        // A missing address must not read as "nothing to compare, carry on".
        var quote = Quote(lockupAddress: null);

        Assert.Throws<LockupAddressMismatchException>(() =>
            LightningSendGates.VerifyLockupAddress(quote, "ark1qlockup"));
    }

    private static RfqQuote<LightningSendQuoteProfile> Quote(
        long? validUntil = null,
        long? refundLocktime = null,
        long? fromAmount = null,
        long? toAmount = null,
        string? lockupAddress = "ark1qlockup") => new()
    {
        RfqId = "9f2c00000000000000000000000000000000000000000000000000000000a1b2",
        Pair = LightningSendProfile.Pair,
        SolverPubkey = "ae75000000000000000000000000000000000000000000000000000000000009",
        ValidUntil = validUntil ?? Now + 900,
        RefundLocktime = refundLocktime ?? Now + 605_184,
        FromAmount = fromAmount ?? InvoiceAmount,
        ToAmount = toAmount ?? InvoiceAmount,
        Profile = new LightningSendQuoteProfile { LockupAddress = lockupAddress },
    };
}
