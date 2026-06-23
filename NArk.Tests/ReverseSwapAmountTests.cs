using NArk.Swaps.Boltz;
using NArk.Swaps.Models;

namespace NArk.Tests;

public class ReverseSwapAmountTests
{
    [Test]
    public void BuildReverseAmounts_Recipient_PinsInvoiceAmount()
    {
        var (invoice, onchain) = BoltzSwapService.BuildReverseAmounts(ReverseSwapFeePayer.Recipient, 75_000);
        Assert.That(invoice, Is.EqualTo(75_000));
        Assert.That(onchain, Is.Null);
    }

    [Test]
    public void BuildReverseAmounts_Sender_PinsOnchainAmount()
    {
        var (invoice, onchain) = BoltzSwapService.BuildReverseAmounts(ReverseSwapFeePayer.Sender, 75_000);
        Assert.That(invoice, Is.Null);
        Assert.That(onchain, Is.EqualTo(75_000));
    }

    [Test]
    public void ValidateReverseAmounts_Recipient_ExactInvoiceAndFeeDeducted_DoesNotThrow()
    {
        // 75_000 requested, invoice == 75_000, Boltz reports receiver nets 74_700 (0.4% fee)
        Assert.DoesNotThrow(() =>
            BoltzSwapService.ValidateReverseAmounts(ReverseSwapFeePayer.Recipient, 75_000, 75_000, 74_700));
    }

    [Test]
    public void ValidateReverseAmounts_Recipient_InflatedInvoice_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            BoltzSwapService.ValidateReverseAmounts(ReverseSwapFeePayer.Recipient, 75_000, 75_302, 75_000));
    }

    [Test]
    public void ValidateReverseAmounts_Recipient_InvoiceBelowRequested_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            BoltzSwapService.ValidateReverseAmounts(ReverseSwapFeePayer.Recipient, 75_000, 74_999, 74_700));
    }

    [Test]
    public void ValidateReverseAmounts_Recipient_MissingOnchain_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            BoltzSwapService.ValidateReverseAmounts(ReverseSwapFeePayer.Recipient, 75_000, 75_000, null));
    }

    [Test]
    public void ValidateReverseAmounts_Recipient_NonPositiveOnchain_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            BoltzSwapService.ValidateReverseAmounts(ReverseSwapFeePayer.Recipient, 75_000, 75_000, 0));
    }

    [Test]
    public void ValidateReverseAmounts_Recipient_OnchainExceedsInvoice_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            BoltzSwapService.ValidateReverseAmounts(ReverseSwapFeePayer.Recipient, 75_000, 75_000, 75_001));
    }

    [Test]
    public void ValidateReverseAmounts_Sender_InflatedInvoiceAndNoOnchain_DoesNotThrow()
    {
        Assert.DoesNotThrow(() =>
            BoltzSwapService.ValidateReverseAmounts(ReverseSwapFeePayer.Sender, 75_000, 75_302, null));
    }

    [Test]
    public void ValidateReverseAmounts_Sender_ZeroFeeInvoiceEqualsRequested_DoesNotThrow()
    {
        Assert.DoesNotThrow(() =>
            BoltzSwapService.ValidateReverseAmounts(ReverseSwapFeePayer.Sender, 75_000, 75_000, null));
    }

    [Test]
    public void ValidateReverseAmounts_Sender_InvoiceBelowRequested_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            BoltzSwapService.ValidateReverseAmounts(ReverseSwapFeePayer.Sender, 75_000, 74_999, null));
    }

    [Test]
    public void ValidateReverseAmounts_Sender_EchoedOnchainMatchesPin_DoesNotThrow()
    {
        Assert.DoesNotThrow(() =>
            BoltzSwapService.ValidateReverseAmounts(ReverseSwapFeePayer.Sender, 75_000, 75_302, 75_000));
    }

    [Test]
    public void ValidateReverseAmounts_Sender_EchoedOnchainDiffersFromPin_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            BoltzSwapService.ValidateReverseAmounts(ReverseSwapFeePayer.Sender, 75_000, 75_302, 74_000));
    }

    [Test]
    public void ResolveExpectedOnchainAmount_Recipient_UsesBoltzReportedAmount()
    {
        Assert.That(
            BoltzSwapService.ResolveExpectedOnchainAmount(ReverseSwapFeePayer.Recipient, 75_000, 74_700),
            Is.EqualTo(74_700));
    }

    [Test]
    public void ResolveExpectedOnchainAmount_Recipient_MissingAmount_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            BoltzSwapService.ResolveExpectedOnchainAmount(ReverseSwapFeePayer.Recipient, 75_000, null));
    }

    [Test]
    public void ResolveExpectedOnchainAmount_Sender_UsesRequestedAmount()
    {
        Assert.That(
            BoltzSwapService.ResolveExpectedOnchainAmount(ReverseSwapFeePayer.Sender, 75_000, null),
            Is.EqualTo(75_000));
    }
}
