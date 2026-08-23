using NArk.Swaps.Boltz;
using NArk.Swaps.Models;
using static NArk.Swaps.Boltz.BoltzOperationClassifier;
using static NArk.Swaps.Boltz.BoltzSwapStatus;

namespace NArk.Tests;

/// <summary>
/// Unit tests for <see cref="BoltzOperationClassifier.Classify"/>.
///
/// The classifier is the single routing decision point for all swap actions
/// (refund, renegotiate, claim, cross-sign). It replaced ~60 lines of inline
/// conditionals in PollSwapState. A bug here silently skips fund recovery or
/// double-triggers claims — hence explicit coverage of every branch.
///
/// Status semantics per https://api.docs.boltz.exchange/lifecycle.html:
///   Refundable statuses (submarine):  invoice.failedToPay, swap.expired, transaction.lockupFailed
///   Refundable statuses (chain):      swap.expired only
///   Renegotiable (chain only):        transaction.lockupFailed
///   Claim trigger (ARK→BTC chain):    transaction.server.mempool, transaction.server.confirmed
///   Sign trigger  (BTC→ARK chain):    transaction.claim.pending
/// </summary>
[TestFixture]
public class BoltzOperationClassifierTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static ArkSwap MakeSwap(ArkSwapType type, ArkSwapStatus status = ArkSwapStatus.Pending) => new(
        SwapId: "swap-test",
        WalletId: "wallet-test",
        SwapType: type,
        Invoice: "",
        ExpectedAmount: 50_000,
        ContractScript: "5120abcd",
        Address: "tark1...",
        Status: status,
        FailReason: null,
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow,
        Hash: "hash");

    // ── Submarine refund ─────────────────────────────────────────────────────

    [TestCase(InvoiceFailedToPay)]
    [TestCase(SwapExpired)]
    [TestCase(TransactionLockupFailed)]
    public void Submarine_RefundableStatus_ReturnsCanCoopRefundSubmarine(string boltzStatus)
    {
        var swap = MakeSwap(ArkSwapType.Submarine);
        Assert.That(Classify(swap, boltzStatus), Is.EqualTo(BoltzSwapAction.CanCoopRefundSubmarine));
    }

    [TestCase(SwapCreated)]
    [TestCase(InvoiceSet)]
    [TestCase(InvoicePending)]
    [TestCase(InvoicePaid)]
    [TestCase(TransactionMempool)]
    [TestCase(TransactionConfirmed)]
    [TestCase(TransactionClaimPending)]
    [TestCase(TransactionClaimed)]
    public void Submarine_NonRefundableStatus_ReturnsNull(string boltzStatus)
    {
        var swap = MakeSwap(ArkSwapType.Submarine);
        Assert.That(Classify(swap, boltzStatus), Is.Null);
    }

    [TestCase(InvoiceFailedToPay)]
    [TestCase(SwapExpired)]
    public void Submarine_AlreadyRefunded_ReturnsNull(string boltzStatus)
    {
        var swap = MakeSwap(ArkSwapType.Submarine, ArkSwapStatus.Refunded);
        Assert.That(Classify(swap, boltzStatus), Is.Null);
    }

    [TestCase(InvoiceFailedToPay)]
    [TestCase(SwapExpired)]
    public void Submarine_AlreadySettled_ReturnsNull(string boltzStatus)
    {
        var swap = MakeSwap(ArkSwapType.Submarine, ArkSwapStatus.Settled);
        Assert.That(Classify(swap, boltzStatus), Is.Null);
    }

    // ── Chain ARK→BTC refund ─────────────────────────────────────────────────

    [Test]
    public void ChainArkToBtc_SwapExpired_ReturnsCanCoopRefundArkToBtc()
    {
        var swap = MakeSwap(ArkSwapType.ChainArkToBtc);
        Assert.That(Classify(swap, SwapExpired), Is.EqualTo(BoltzSwapAction.CanCoopRefundArkToBtc));
    }

    [Test]
    public void ChainArkToBtc_SwapExpired_AlreadyRefunded_ReturnsNull()
    {
        var swap = MakeSwap(ArkSwapType.ChainArkToBtc, ArkSwapStatus.Refunded);
        Assert.That(Classify(swap, SwapExpired), Is.Null);
    }

    [Test]
    public void ChainArkToBtc_SwapExpired_AlreadySettled_ReturnsNull()
    {
        var swap = MakeSwap(ArkSwapType.ChainArkToBtc, ArkSwapStatus.Settled);
        Assert.That(Classify(swap, SwapExpired), Is.Null);
    }

    // lockupFailed on ARK→BTC → renegotiation, not refund
    [Test]
    public void ChainArkToBtc_LockupFailed_ReturnsCanRenegotiateChain()
    {
        var swap = MakeSwap(ArkSwapType.ChainArkToBtc);
        Assert.That(Classify(swap, TransactionLockupFailed), Is.EqualTo(BoltzSwapAction.CanRenegotiateChain));
    }

    // ── Chain BTC→ARK refund ─────────────────────────────────────────────────

    [Test]
    public void ChainBtcToArk_SwapExpired_ReturnsCanCoopRefundBtcToArk()
    {
        var swap = MakeSwap(ArkSwapType.ChainBtcToArk);
        Assert.That(Classify(swap, SwapExpired), Is.EqualTo(BoltzSwapAction.CanCoopRefundBtcToArk));
    }

    [Test]
    public void ChainBtcToArk_SwapExpired_AlreadyRefunded_ReturnsNull()
    {
        var swap = MakeSwap(ArkSwapType.ChainBtcToArk, ArkSwapStatus.Refunded);
        Assert.That(Classify(swap, SwapExpired), Is.Null);
    }

    // lockupFailed on BTC→ARK → renegotiation, not refund
    [Test]
    public void ChainBtcToArk_LockupFailed_ReturnsCanRenegotiateChain()
    {
        var swap = MakeSwap(ArkSwapType.ChainBtcToArk);
        Assert.That(Classify(swap, TransactionLockupFailed), Is.EqualTo(BoltzSwapAction.CanRenegotiateChain));
    }

    // ── Renegotiation (chain swaps only) ─────────────────────────────────────

    [TestCase(ArkSwapType.ChainArkToBtc)]
    [TestCase(ArkSwapType.ChainBtcToArk)]
    public void Chain_LockupFailed_Pending_ReturnsCanRenegotiateChain(ArkSwapType type)
    {
        var swap = MakeSwap(type);
        Assert.That(Classify(swap, TransactionLockupFailed), Is.EqualTo(BoltzSwapAction.CanRenegotiateChain));
    }

    [Test]
    public void Submarine_LockupFailed_ReturnsCanCoopRefundSubmarine_NotRenegotiate()
    {
        // Per docs: renegotiation applies only to chain swaps, not submarine.
        // Submarine gets cooperative refund instead.
        var swap = MakeSwap(ArkSwapType.Submarine);
        Assert.That(Classify(swap, TransactionLockupFailed), Is.EqualTo(BoltzSwapAction.CanCoopRefundSubmarine));
    }

    [TestCase(ArkSwapType.ChainArkToBtc)]
    [TestCase(ArkSwapType.ChainBtcToArk)]
    public void Chain_LockupFailed_AlreadySettled_ReturnsNull(ArkSwapType type)
    {
        var swap = MakeSwap(type, ArkSwapStatus.Settled);
        Assert.That(Classify(swap, TransactionLockupFailed), Is.Null);
    }

    // ── Claiming (ARK→BTC chain) ──────────────────────────────────────────────

    [TestCase(TransactionServerMempool)]
    [TestCase(TransactionServerConfirmed)]
    public void ChainArkToBtc_ServerLocked_ReturnsCanClaimChain(string boltzStatus)
    {
        var swap = MakeSwap(ArkSwapType.ChainArkToBtc);
        Assert.That(Classify(swap, boltzStatus), Is.EqualTo(BoltzSwapAction.CanClaimChain));
    }

    // BTC→ARK should NOT claim on server.mempool — that's cross-sign territory
    [TestCase(TransactionServerMempool)]
    [TestCase(TransactionServerConfirmed)]
    public void ChainBtcToArk_ServerLocked_ReturnsNull(string boltzStatus)
    {
        var swap = MakeSwap(ArkSwapType.ChainBtcToArk);
        Assert.That(Classify(swap, boltzStatus), Is.Null);
    }

    [TestCase(TransactionServerMempool)]
    [TestCase(TransactionServerConfirmed)]
    public void Submarine_ServerLocked_ReturnsNull(string boltzStatus)
    {
        var swap = MakeSwap(ArkSwapType.Submarine);
        Assert.That(Classify(swap, boltzStatus), Is.Null);
    }

    // ── Cross-signing (BTC→ARK chain) ────────────────────────────────────────

    [Test]
    public void ChainBtcToArk_ClaimPending_ReturnsReadyToSignClaim()
    {
        var swap = MakeSwap(ArkSwapType.ChainBtcToArk);
        Assert.That(Classify(swap, TransactionClaimPending), Is.EqualTo(BoltzSwapAction.ReadyToSignClaim));
    }

    [Test]
    public void ChainArkToBtc_ClaimPending_ReturnsNull()
    {
        // ARK→BTC does cooperative claim, not cross-sign
        var swap = MakeSwap(ArkSwapType.ChainArkToBtc);
        Assert.That(Classify(swap, TransactionClaimPending), Is.Null);
    }

    [Test]
    public void Submarine_ClaimPending_ReturnsNull()
    {
        var swap = MakeSwap(ArkSwapType.Submarine);
        Assert.That(Classify(swap, TransactionClaimPending), Is.Null);
    }

    // ── Terminal swaps — no action regardless of Boltz status ────────────────

    [TestCase(SwapExpired)]
    [TestCase(InvoiceFailedToPay)]
    [TestCase(TransactionLockupFailed)]
    [TestCase(TransactionServerMempool)]
    [TestCase(TransactionClaimPending)]
    public void AnyType_AlreadySettled_ReturnsNull(string boltzStatus)
    {
        foreach (var type in new[] { ArkSwapType.Submarine, ArkSwapType.ChainArkToBtc, ArkSwapType.ChainBtcToArk })
        {
            var swap = MakeSwap(type, ArkSwapStatus.Settled);
            Assert.That(Classify(swap, boltzStatus), Is.Null,
                $"{type} + {boltzStatus} should be null when already Settled");
        }
    }

    // ── Non-actionable statuses return null ──────────────────────────────────

    [TestCase(SwapCreated)]
    [TestCase(TransactionMempool)]
    [TestCase(TransactionConfirmed)]
    [TestCase(TransactionClaimed)]
    [TestCase(TransactionRefunded)]
    [TestCase(InvoiceSettled)]
    [TestCase(MinerFeePaid)]
    public void AnyType_NonActionableStatus_ReturnsNull(string boltzStatus)
    {
        foreach (var type in new[] { ArkSwapType.Submarine, ArkSwapType.ChainArkToBtc, ArkSwapType.ChainBtcToArk })
        {
            var swap = MakeSwap(type);
            Assert.That(Classify(swap, boltzStatus), Is.Null,
                $"{type} + {boltzStatus} should not trigger any action");
        }
    }
}
