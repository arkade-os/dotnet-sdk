using System.Security.Cryptography;
using BTCPayServer.Lightning;
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
/// The receive client's checks on the solver's invoice.
/// </summary>
/// <remarks>
/// This is the corridor where the client hands a stranger something to pay, so the invoice is the
/// one artefact that leaves the client's control and moves someone else's money. Every way it can
/// be wrong has to be refused before it is published, not after.
/// </remarks>
[TestFixture]
public class LightningReceiveGatesTests
{
    private const long AmountSats = 50_000;

    [Test]
    public void VerifyInvoice_AcceptsAnInvoiceMatchingTheRequest()
    {
        var decoded = LightningReceiveGates.VerifyInvoice(
            Quote(Invoice, AmountSats), PaymentHashOfInvoice, AmountSats, RfqAmountSide.To, Network.RegTest);

        Assert.That(decoded.PaymentHash.ToString().ToLowerInvariant(), Is.EqualTo(PaymentHashOfInvoice));
    }

    [Test]
    public void VerifyInvoice_RefusesAHashOurPreimageCouldNeverSettle()
    {
        // The solver mints against the hash we sent. One for a different hash would take the payer's
        // money into an HTLC our secret cannot open.
        var somebodyElsesHash = new string('a', 64);

        var ex = Assert.Throws<LightningReceiveNotUsableException>(() =>
            LightningReceiveGates.VerifyInvoice(Quote(Invoice, AmountSats), somebodyElsesHash, AmountSats, RfqAmountSide.To, Network.RegTest));

        Assert.That(ex!.Reason, Is.EqualTo(LightningReceiveRefusalReason.WrongPaymentHash));
    }

    [Test]
    public void VerifyInvoice_RefusesAnInvoiceThatDisagreesWithWhatThePayerOwes()
    {
        // `from_amount` is what the payer owes, so it is what the invoice must say. A mismatch means
        // the invoice and the quote describe different trades.
        var ex = Assert.Throws<LightningReceiveNotUsableException>(() =>
            LightningReceiveGates.VerifyInvoice(
                Quote(Invoice, AmountSats, fromAmount: AmountSats + 1_000),
                PaymentHashOfInvoice, AmountSats, RfqAmountSide.To, Network.RegTest));

        Assert.That(ex!.Reason, Is.EqualTo(LightningReceiveRefusalReason.AmountMismatch));
    }

    [Test]
    public void VerifyInvoice_AcceptsAFeeBetweenWhatThePayerOwesAndWhatWeReceive()
    {
        // The whole point of the spread: the payer is billed more than lands on Arkade. Checking the
        // invoice against the payout instead would refuse every quote that charges anything.
        var withFee = Quote(Invoice, toAmount: AmountSats - 500, fromAmount: AmountSats);

        Assert.DoesNotThrow(() =>
            LightningReceiveGates.VerifyInvoice(
                withFee, PaymentHashOfInvoice, AmountSats - 500, RfqAmountSide.To, Network.RegTest));
    }

    [Test]
    public void VerifyInvoice_RefusesAQuoteThatDeliversLessThanAskedFor()
    {
        // The other half of the same trap: a solver could bill the payer correctly and still short
        // the payout. Nothing else in the flow would notice.
        var ex = Assert.Throws<LightningReceiveNotUsableException>(() =>
            LightningReceiveGates.VerifyInvoice(
                Quote(Invoice, toAmount: AmountSats - 500, fromAmount: AmountSats),
                PaymentHashOfInvoice, AmountSats, RfqAmountSide.To, Network.RegTest));

        Assert.That(ex!.Reason, Is.EqualTo(LightningReceiveRefusalReason.ShortPayout));
    }

    // ─── The leg the request pinned ───────────────────────────────────

    [Test]
    public void VerifyInvoice_ExactIn_AcceptsAPayoutReducedByTheFee()
    {
        // Pinning the from leg is what a merchant does: the invoice is for the order total, and the
        // spread comes out of what lands on Arkade. The payout is BELOW the number asked for, which
        // is exactly what the exact-out check would have refused.
        var quote = Quote(Invoice, toAmount: AmountSats - 500, fromAmount: AmountSats);

        Assert.DoesNotThrow(() =>
            LightningReceiveGates.VerifyInvoice(
                quote, PaymentHashOfInvoice, AmountSats, RfqAmountSide.From, Network.RegTest));
    }

    [Test]
    public void VerifyInvoice_ExactIn_RefusesAnInvoiceForMoreThanWasAskedFor()
    {
        // The failure this whole parameter exists to prevent. An invoice above the order total is
        // one a LUD-06 wallet refuses, so the customer cannot pay it and the sale is simply lost —
        // and on a checkout that does not run that check, they are silently overcharged instead.
        // Read the other way round from the fixture: the invoice bills AmountSats, and we asked for
        // 500 less than that.
        var quote = Quote(Invoice, toAmount: AmountSats - 500, fromAmount: AmountSats);

        var ex = Assert.Throws<LightningReceiveNotUsableException>(() =>
            LightningReceiveGates.VerifyInvoice(
                quote, PaymentHashOfInvoice, AmountSats - 500, RfqAmountSide.From, Network.RegTest));

        Assert.That(ex!.Reason, Is.EqualTo(LightningReceiveRefusalReason.PayerChargeMismatch));
    }

    [Test]
    public void VerifyInvoice_ExactIn_RefusesAnInvoiceForLessThanWasAskedFor()
    {
        // Under-billing is refused just as firmly as over-billing: the order would settle short, and
        // nothing downstream would report it as anything other than paid.
        var quote = Quote(Invoice, toAmount: AmountSats - 500, fromAmount: AmountSats);

        var ex = Assert.Throws<LightningReceiveNotUsableException>(() =>
            LightningReceiveGates.VerifyInvoice(
                quote, PaymentHashOfInvoice, AmountSats + 500, RfqAmountSide.From, Network.RegTest));

        Assert.That(ex!.Reason, Is.EqualTo(LightningReceiveRefusalReason.PayerChargeMismatch));
    }

    [Test]
    public void VerifyInvoice_ExactIn_DoesNotApplyTheExactOutPayoutFloor()
    {
        // The two checks are mutually exclusive, not cumulative. Running both would refuse every
        // exact-in quote that charged a fee, which is every exact-in quote.
        var quote = Quote(Invoice, toAmount: 1, fromAmount: AmountSats);

        Assert.DoesNotThrow(() =>
            LightningReceiveGates.VerifyInvoice(
                quote, PaymentHashOfInvoice, AmountSats, RfqAmountSide.From, Network.RegTest));
    }

    [Test]
    public void VerifyInvoice_RefusesAQuoteWithNoInvoiceAtAll()
    {
        var ex = Assert.Throws<LightningReceiveNotUsableException>(() =>
            LightningReceiveGates.VerifyInvoice(Quote(null, AmountSats), new string('b', 64), AmountSats, RfqAmountSide.To, Network.RegTest));

        Assert.That(ex!.Reason, Is.EqualTo(LightningReceiveRefusalReason.UnusableInvoice));
    }

    [Test]
    public void VerifyInvoice_RefusesSomethingThatIsNotABolt11()
    {
        var ex = Assert.Throws<LightningReceiveNotUsableException>(() =>
            LightningReceiveGates.VerifyInvoice(Quote("not-an-invoice", AmountSats), new string('c', 64), AmountSats, RfqAmountSide.To, Network.RegTest));

        Assert.That(ex!.Reason, Is.EqualTo(LightningReceiveRefusalReason.UnusableInvoice));
    }

    // ─── AssertReceivable: the deadlines around paying ────────────────

    [Test]
    public void AssertReceivable_AcceptsAWindowWideEnoughToClaimIn()
    {
        var invoice = BOLT11PaymentRequest.Parse(Invoice, Network.RegTest);
        var expiry = invoice.ExpiryDate.ToUnixTimeSeconds();
        var quote = Quote(Invoice, AmountSats, validUntil: expiry + 600, refundLocktime: expiry + 3600);

        var payDeadline = LightningReceiveGates.AssertReceivable(quote, invoice, expiry - 600);

        Assert.That(payDeadline, Is.EqualTo(expiry));
    }

    [Test]
    public void AssertReceivable_RefusesAnExpiredInvoiceOrQuote()
    {
        var invoice = BOLT11PaymentRequest.Parse(Invoice, Network.RegTest);
        var expiry = invoice.ExpiryDate.ToUnixTimeSeconds();
        var quote = Quote(Invoice, AmountSats, validUntil: expiry + 600, refundLocktime: expiry + 7200);

        var ex = Assert.Throws<LightningReceiveNotUsableException>(() =>
            LightningReceiveGates.AssertReceivable(quote, invoice, expiry));

        Assert.That(ex!.Reason, Is.EqualTo(LightningReceiveRefusalReason.Expired));
    }

    [Test]
    public void AssertReceivable_RefusesAClaimWindowTooShortToDeliverIn()
    {
        // A payment at the deadline must still leave room to claim before the solver's reclaim
        // opens — otherwise the payer's money sits in a held HTLC until it lapses.
        var invoice = BOLT11PaymentRequest.Parse(Invoice, Network.RegTest);
        var expiry = invoice.ExpiryDate.ToUnixTimeSeconds();
        var quote = Quote(Invoice, AmountSats, validUntil: expiry, refundLocktime: expiry + 60);

        var ex = Assert.Throws<LightningReceiveNotUsableException>(() =>
            LightningReceiveGates.AssertReceivable(quote, invoice, expiry - 600));

        Assert.That(ex!.Reason, Is.EqualTo(LightningReceiveRefusalReason.ClaimWindowTooShort));
    }

    [Test]
    public void AssertReceivable_TakesTheEarlierOfInvoiceExpiryAndValidUntil()
    {
        var invoice = BOLT11PaymentRequest.Parse(Invoice, Network.RegTest);
        var expiry = invoice.ExpiryDate.ToUnixTimeSeconds();
        var quote = Quote(Invoice, AmountSats, validUntil: expiry - 900, refundLocktime: expiry + 3600);

        var payDeadline = LightningReceiveGates.AssertReceivable(quote, invoice, expiry - 1800);

        Assert.That(payDeadline, Is.EqualTo(expiry - 900));
    }

    [Test]
    public void AssertReceivable_RefusesAQuoteBillingThePayerAboveTheCeiling()
    {
        // Pinning what lands on Arkade leaves the payer's side to the solver. Without a ceiling
        // nothing bounds it, and the invoice a customer is handed is the first place it shows.
        var invoice = BOLT11PaymentRequest.Parse(Invoice, Network.RegTest);
        var expiry = invoice.ExpiryDate.ToUnixTimeSeconds();
        var quote = Quote(Invoice, AmountSats, fromAmount: 80_000,
            validUntil: expiry + 600, refundLocktime: expiry + 3600);

        var ex = Assert.Throws<LightningReceiveNotUsableException>(() =>
            LightningReceiveGates.AssertReceivable(quote, invoice, expiry - 600, maxPayAmountSats: 60_000));

        Assert.That(ex!.Reason, Is.EqualTo(LightningReceiveRefusalReason.PriceTooHigh));
    }

    [Test]
    public void AssertReceivable_AcceptsAQuoteAtTheCeiling()
    {
        var invoice = BOLT11PaymentRequest.Parse(Invoice, Network.RegTest);
        var expiry = invoice.ExpiryDate.ToUnixTimeSeconds();
        var quote = Quote(Invoice, AmountSats, fromAmount: 60_000,
            validUntil: expiry + 600, refundLocktime: expiry + 3600);

        Assert.DoesNotThrow(() =>
            LightningReceiveGates.AssertReceivable(quote, invoice, expiry - 600, maxPayAmountSats: 60_000));
    }

    [Test]
    public void AssertReceivable_WithNoCeiling_DoesNotBoundThePayersSide()
    {
        // The default stays permissive: a deployment that pins the payer's leg does not need this,
        // and turning it on silently would refuse quotes that were always fine.
        var invoice = BOLT11PaymentRequest.Parse(Invoice, Network.RegTest);
        var expiry = invoice.ExpiryDate.ToUnixTimeSeconds();
        var quote = Quote(Invoice, AmountSats, fromAmount: 10_000_000,
            validUntil: expiry + 600, refundLocktime: expiry + 3600);

        Assert.DoesNotThrow(() => LightningReceiveGates.AssertReceivable(quote, invoice, expiry - 600));
    }

    // ─── ResolveLockupContract: which shape the solver will actually fund ─────

    [Test]
    public void ResolveLockupContract_AcceptsTheEightLeafShapeWhenItMatches()
    {
        var (eightLeaf, nineLeaf) = Candidates();
        var quote = Quote(Invoice, AmountSats, lockupAddress: eightLeaf.GetArkAddress().ToString(false));

        var resolved = LightningReceiveGates.ResolveLockupContract(quote, eightLeaf, nineLeaf, isMainnet: false);

        Assert.That(resolved, Is.SameAs(eightLeaf));
    }

    [Test]
    public void ResolveLockupContract_AcceptsTheNineLeafShapeWhenItMatches()
    {
        // A solver that has turned on NonInteractiveRefundWithoutReceiver funds an address this
        // client would never have derived on its own before — the swap must still be recognised.
        var (eightLeaf, nineLeaf) = Candidates();
        var quote = Quote(Invoice, AmountSats, lockupAddress: nineLeaf.GetArkAddress().ToString(false));

        var resolved = LightningReceiveGates.ResolveLockupContract(quote, eightLeaf, nineLeaf, isMainnet: false);

        Assert.That(resolved, Is.SameAs(nineLeaf));
    }

    [Test]
    public void ResolveLockupContract_ThrowsWhenTheQuoteMatchesNeitherShape()
    {
        // The refusal that must never soften: on this corridor the SOLVER funds the lockup, so
        // accepting an address matching neither derivation would have the client import and watch a
        // script nothing says the solver will ever pay.
        var (eightLeaf, nineLeaf) = Candidates();
        var quote = Quote(Invoice, AmountSats, lockupAddress: "ark1qsomewhere-else");

        var ex = Assert.Throws<LockupAddressMismatchException>(() =>
            LightningReceiveGates.ResolveLockupContract(quote, eightLeaf, nineLeaf, isMainnet: false));

        Assert.That(ex!.DerivedEightLeaf, Is.EqualTo(eightLeaf.GetArkAddress().ToString(false)));
        Assert.That(ex.DerivedNineLeaf, Is.EqualTo(nineLeaf.GetArkAddress().ToString(false)));
        Assert.That(ex.Quoted, Is.EqualTo("ark1qsomewhere-else"));
    }

    [Test]
    public void ResolveLockupContract_DefaultsToTheEightLeafShapeWhenTheSolverQuotesNoAddressAtAll()
    {
        // Unlike the send leg, an absent lockup_address is not itself a refusal on this corridor —
        // see LightningReceiveQuoteProfile.LockupAddress. With nothing to compare against, this keeps
        // the same shape the corridor always defaulted to before the ninth leaf existed, rather than
        // guessing the opt-in one.
        var (eightLeaf, nineLeaf) = Candidates();
        var quote = Quote(Invoice, AmountSats, lockupAddress: null);

        var resolved = LightningReceiveGates.ResolveLockupContract(quote, eightLeaf, nineLeaf, isMainnet: false);

        Assert.That(resolved, Is.SameAs(eightLeaf));
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

    private static RfqQuote<LightningReceiveQuoteProfile> Quote(
        string? invoice, long toAmount, long? fromAmount = null,
        long validUntil = 1_800_000_900, long refundLocktime = 1_800_605_184,
        string? lockupAddress = null) => new()
    {
        RfqId = new string('9', 64),
        Pair = LightningReceiveProfile.Pair,
        FromAmount = fromAmount ?? toAmount,
        ToAmount = toAmount,
        SolverPubkey = new string('e', 64),
        ValidUntil = validUntil,
        RefundLocktime = refundLocktime,
        Profile = new LightningReceiveQuoteProfile { Invoice = invoice, LockupAddress = lockupAddress },
    };

    /// <summary>
    /// A real regtest BOLT11 for <see cref="AmountSats"/>, so the checks run against something that
    /// genuinely decodes rather than a stub that cannot fail the way a real one does.
    /// </summary>
    private const string Invoice =
        "lnbcrt500000n1p480splpp5curkvkad252gsynvrp5fj4q7m9rhh4cq5n085v6nfdfrpgzdpe9qxqr8pqcqpj4q5dgd" +
        "snq6g3gpln7hxmy44dkzesuejn5hg3qjvw7m55a7qgj5d4a98807l23tr2xsy3k0ula655raayenf5l88twhhsv2lu2nt0l5qpvm7x0y";

    /// <summary>
    /// The invoice's payment hash the way a payment hash is actually written.
    /// </summary>
    /// <remarks>
    /// Deliberately <c>ToString()</c> and not <c>ToBytes()</c>. <see cref="uint256"/> hands out its
    /// bytes little-endian, and an earlier version of this fixture took them that way — the same call
    /// the code under test was making, so the two agreed on a reversed hash and the test could not
    /// have failed on it. It took a real solver to disagree. Building the expectation the way the
    /// counterparty writes it is what makes this a check rather than an echo.
    /// </remarks>
    private static readonly string PaymentHashOfInvoice =
        BOLT11PaymentRequest.Parse(Invoice, Network.RegTest).PaymentHash.ToString().ToLowerInvariant();
}
