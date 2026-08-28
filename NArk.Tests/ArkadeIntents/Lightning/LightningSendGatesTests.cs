using System.Security.Cryptography;
using NArk.Abstractions.Extensions;
using NArk.Arkade.Contracts;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Lightning;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

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
    public void ResolveLockupContract_AcceptsTheEightLeafShapeWhenItMatches()
    {
        var (eightLeaf, nineLeaf) = Candidates();
        var quote = Quote(lockupAddress: eightLeaf.GetArkAddress().ToString(false));

        var resolved = LightningSendGates.ResolveLockupContract(quote, eightLeaf, nineLeaf, isMainnet: false);

        Assert.That(resolved, Is.SameAs(eightLeaf));
    }

    [Test]
    public void ResolveLockupContract_AcceptsTheNineLeafShapeWhenItMatches()
    {
        // The whole point: a solver that has turned on NonInteractiveRefundWithoutReceiver quotes an
        // address this client would never have derived on its own before, and the swap must still
        // go through rather than refuse a perfectly good quote.
        var (eightLeaf, nineLeaf) = Candidates();
        var quote = Quote(lockupAddress: nineLeaf.GetArkAddress().ToString(false));

        var resolved = LightningSendGates.ResolveLockupContract(quote, eightLeaf, nineLeaf, isMainnet: false);

        Assert.That(resolved, Is.SameAs(nineLeaf));
    }

    [Test]
    public void ResolveLockupContract_ThrowsWhenTheQuoteMatchesNeitherShape()
    {
        // The refusal that must never soften: an address matching neither of our own derivations
        // must never be accepted, or a wrong or malicious solver could walk the maker into funding a
        // script nobody here can rebuild.
        var (eightLeaf, nineLeaf) = Candidates();
        var quote = Quote(lockupAddress: "ark1qsomewhere-else");

        var ex = Assert.Throws<LockupAddressMismatchException>(() =>
            LightningSendGates.ResolveLockupContract(quote, eightLeaf, nineLeaf, isMainnet: false));

        Assert.That(ex!.DerivedEightLeaf, Is.EqualTo(eightLeaf.GetArkAddress().ToString(false)));
        Assert.That(ex.DerivedNineLeaf, Is.EqualTo(nineLeaf.GetArkAddress().ToString(false)));
        Assert.That(ex.Quoted, Is.EqualTo("ark1qsomewhere-else"));
    }

    [Test]
    public void ResolveLockupContract_ThrowsWhenTheSolverSentNoneAtAll()
    {
        // A missing address must not read as "nothing to compare, carry on" — true of one candidate
        // before, and just as true of two now.
        var (eightLeaf, nineLeaf) = Candidates();
        var quote = Quote(lockupAddress: null);

        Assert.Throws<LockupAddressMismatchException>(() =>
            LightningSendGates.ResolveLockupContract(quote, eightLeaf, nineLeaf, isMainnet: false));
    }

    /// <summary>Two lockup shapes built from one shared, otherwise-arbitrary parameter set.</summary>
    private static (VHTLCv2Contract EightLeaf, VHTLCv2Contract NineLeaf) Candidates() =>
        LightningCorridor.DeriveBothLockupShapes(
            RandomDescriptor(),
            RandomDescriptor(),
            RandomDescriptor(),
            new uint160(RandomNumberGenerator.GetBytes(20), false),
            new LockTime(1_800_600_000),
            new Sequence(TimeSpan.FromSeconds(512)),
            new Sequence(TimeSpan.FromSeconds(512)),
            new Sequence(TimeSpan.FromSeconds(1024)),
            RandomXOnly(),
            RandomP2trPkScript(),
            RandomP2trPkScript());

    private static OutputDescriptor RandomDescriptor() =>
        KeyExtensions.ParseOutputDescriptor(new Key().PubKey.ToHex(), Network.RegTest);

    private static ECXOnlyPubKey RandomXOnly() =>
        ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes());

    private static byte[] RandomP2trPkScript()
    {
        var script = new byte[34];
        script[0] = 0x51;
        script[1] = 0x20;
        new Key().PubKey.TaprootInternalKey.ToBytes().CopyTo(script, 2);
        return script;
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
