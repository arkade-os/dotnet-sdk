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
            Quote(Invoice, AmountSats), PaymentHashOfInvoice, Network.RegTest);

        Assert.That(
            Convert.ToHexString(decoded.PaymentHash.ToBytes()).ToLowerInvariant(),
            Is.EqualTo(PaymentHashOfInvoice));
    }

    [Test]
    public void VerifyInvoice_RefusesAHashOurPreimageCouldNeverSettle()
    {
        // The solver mints against the hash we sent. One for a different hash would take the payer's
        // money into an HTLC our secret cannot open.
        var somebodyElsesHash = new string('a', 64);

        var ex = Assert.Throws<LightningReceiveNotUsableException>(() =>
            LightningReceiveGates.VerifyInvoice(Quote(Invoice, AmountSats), somebodyElsesHash, Network.RegTest));

        Assert.That(ex!.Reason, Is.EqualTo(LightningReceiveRefusalReason.WrongPaymentHash));
    }

    [Test]
    public void VerifyInvoice_RefusesAnInvoiceThatDisagreesWithWhatTheQuoteDelivers()
    {
        // The payer acts on the invoice, the swap delivers the quote's amount. If those two ever
        // part company, someone is short by the difference.
        var ex = Assert.Throws<LightningReceiveNotUsableException>(() =>
            LightningReceiveGates.VerifyInvoice(
                Quote(Invoice, AmountSats + 1_000), PaymentHashOfInvoice, Network.RegTest));

        Assert.That(ex!.Reason, Is.EqualTo(LightningReceiveRefusalReason.AmountMismatch));
    }

    [Test]
    public void VerifyInvoice_RefusesAQuoteWithNoInvoiceAtAll()
    {
        var ex = Assert.Throws<LightningReceiveNotUsableException>(() =>
            LightningReceiveGates.VerifyInvoice(Quote(null, AmountSats), new string('b', 64), Network.RegTest));

        Assert.That(ex!.Reason, Is.EqualTo(LightningReceiveRefusalReason.UnusableInvoice));
    }

    [Test]
    public void VerifyInvoice_RefusesSomethingThatIsNotABolt11()
    {
        var ex = Assert.Throws<LightningReceiveNotUsableException>(() =>
            LightningReceiveGates.VerifyInvoice(Quote("not-an-invoice", AmountSats), new string('c', 64), Network.RegTest));

        Assert.That(ex!.Reason, Is.EqualTo(LightningReceiveRefusalReason.UnusableInvoice));
    }

    private static RfqQuote<LightningReceiveQuoteProfile> Quote(string? invoice, long amountSats) => new()
    {
        RfqId = new string('9', 64),
        Pair = LightningReceiveProfile.Pair,
        FromAmount = amountSats,
        ToAmount = amountSats,
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

    private static readonly string PaymentHashOfInvoice =
        Convert.ToHexString(BOLT11PaymentRequest.Parse(Invoice, Network.RegTest).PaymentHash.ToBytes())
            .ToLowerInvariant();
}
