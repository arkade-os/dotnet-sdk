using BTCPayServer.Lightning;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Lightning;
using NBitcoin;

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
            Quote(Invoice, AmountSats), PaymentHashOfInvoice, AmountSats, Network.RegTest);

        Assert.That(decoded.PaymentHash.ToString().ToLowerInvariant(), Is.EqualTo(PaymentHashOfInvoice));
    }

    [Test]
    public void VerifyInvoice_RefusesAHashOurPreimageCouldNeverSettle()
    {
        // The solver mints against the hash we sent. One for a different hash would take the payer's
        // money into an HTLC our secret cannot open.
        var somebodyElsesHash = new string('a', 64);

        var ex = Assert.Throws<LightningReceiveNotUsableException>(() =>
            LightningReceiveGates.VerifyInvoice(Quote(Invoice, AmountSats), somebodyElsesHash, AmountSats, Network.RegTest));

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
                PaymentHashOfInvoice, AmountSats, Network.RegTest));

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
                withFee, PaymentHashOfInvoice, AmountSats - 500, Network.RegTest));
    }

    [Test]
    public void VerifyInvoice_RefusesAQuoteThatDeliversLessThanAskedFor()
    {
        // The other half of the same trap: a solver could bill the payer correctly and still short
        // the payout. Nothing else in the flow would notice.
        var ex = Assert.Throws<LightningReceiveNotUsableException>(() =>
            LightningReceiveGates.VerifyInvoice(
                Quote(Invoice, toAmount: AmountSats - 500, fromAmount: AmountSats),
                PaymentHashOfInvoice, AmountSats, Network.RegTest));

        Assert.That(ex!.Reason, Is.EqualTo(LightningReceiveRefusalReason.ShortPayout));
    }

    [Test]
    public void VerifyInvoice_RefusesAQuoteWithNoInvoiceAtAll()
    {
        var ex = Assert.Throws<LightningReceiveNotUsableException>(() =>
            LightningReceiveGates.VerifyInvoice(Quote(null, AmountSats), new string('b', 64), AmountSats, Network.RegTest));

        Assert.That(ex!.Reason, Is.EqualTo(LightningReceiveRefusalReason.UnusableInvoice));
    }

    [Test]
    public void VerifyInvoice_RefusesSomethingThatIsNotABolt11()
    {
        var ex = Assert.Throws<LightningReceiveNotUsableException>(() =>
            LightningReceiveGates.VerifyInvoice(Quote("not-an-invoice", AmountSats), new string('c', 64), AmountSats, Network.RegTest));

        Assert.That(ex!.Reason, Is.EqualTo(LightningReceiveRefusalReason.UnusableInvoice));
    }

    private static RfqQuote<LightningReceiveQuoteProfile> Quote(
        string? invoice, long toAmount, long? fromAmount = null) => new()
    {
        RfqId = new string('9', 64),
        Pair = LightningReceiveProfile.Pair,
        FromAmount = fromAmount ?? toAmount,
        ToAmount = toAmount,
        SolverPubkey = new string('e', 64),
        ValidUntil = 1_800_000_900,
        RefundLocktime = 1_800_605_184,
        Profile = new LightningReceiveQuoteProfile { Invoice = invoice },
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
