using NArk.Swaps.Boltz;
using NArk.Swaps.Evm;
using NArk.Swaps.Models;
using static NArk.Swaps.Evm.EvmChainOperationClassifier;
using static NArk.Swaps.Boltz.BoltzSwapStatus;

namespace NArk.Tests;

/// <summary>
/// Unit tests for <see cref="EvmChainOperationClassifier.Classify"/>, mirroring
/// <see cref="BoltzOperationClassifierTests"/>'s shape for the EVM chain-swap legs
/// (<see cref="ArkSwapType.ChainArkToEvm"/> / <see cref="ArkSwapType.ChainEvmToArk"/>).
/// </summary>
[TestFixture]
public class EvmChainOperationClassifierTests
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

    // ── Claiming the EVM lockup (ChainArkToEvm: we locked Ark, Boltz locked tBTC) ────

    [TestCase(TransactionServerMempool)]
    [TestCase(TransactionServerConfirmed)]
    public void ChainArkToEvm_ServerLocked_ReturnsCanClaimEvmLockup(string boltzStatus)
    {
        var swap = MakeSwap(ArkSwapType.ChainArkToEvm);
        Assert.That(Classify(swap, boltzStatus), Is.EqualTo(EvmSwapAction.CanClaimEvmLockup));
    }

    // ChainEvmToArk should NOT claim on server.mempool — that side's action is claiming the
    // ARK lockup, not the EVM one (see ChainEvmToArk_ServerLocked_ReturnsCanClaimArkLockup).
    [TestCase(TransactionServerMempool)]
    [TestCase(TransactionServerConfirmed)]
    public void ChainEvmToArk_ServerLocked_DoesNotReturnCanClaimEvmLockup(string boltzStatus)
    {
        var swap = MakeSwap(ArkSwapType.ChainEvmToArk);
        Assert.That(Classify(swap, boltzStatus), Is.Not.EqualTo(EvmSwapAction.CanClaimEvmLockup));
    }

    // ── Claiming the ARK lockup (ChainEvmToArk: we locked tBTC, Boltz locked Ark) ────

    [TestCase(TransactionServerMempool)]
    [TestCase(TransactionServerConfirmed)]
    public void ChainEvmToArk_ServerLocked_ReturnsCanClaimArkLockup(string boltzStatus)
    {
        var swap = MakeSwap(ArkSwapType.ChainEvmToArk);
        Assert.That(Classify(swap, boltzStatus), Is.EqualTo(EvmSwapAction.CanClaimArkLockup));
    }

    [TestCase(TransactionServerMempool)]
    [TestCase(TransactionServerConfirmed)]
    public void ChainArkToEvm_ServerLocked_DoesNotReturnCanClaimArkLockup(string boltzStatus)
    {
        var swap = MakeSwap(ArkSwapType.ChainArkToEvm);
        Assert.That(Classify(swap, boltzStatus), Is.Not.EqualTo(EvmSwapAction.CanClaimArkLockup));
    }

    // ── Refunding our own Ark lockup (ChainArkToEvm, swap expired before Boltz locked tBTC) ─

    [Test]
    public void ChainArkToEvm_SwapExpired_ReturnsCanRefundArkLockup()
    {
        var swap = MakeSwap(ArkSwapType.ChainArkToEvm);
        Assert.That(Classify(swap, SwapExpired), Is.EqualTo(EvmSwapAction.CanRefundArkLockup));
    }

    [Test]
    public void ChainArkToEvm_SwapExpired_AlreadyRefunded_ReturnsNull()
    {
        var swap = MakeSwap(ArkSwapType.ChainArkToEvm, ArkSwapStatus.Refunded);
        Assert.That(Classify(swap, SwapExpired), Is.Null);
    }

    [Test]
    public void ChainArkToEvm_SwapExpired_AlreadySettled_ReturnsNull()
    {
        var swap = MakeSwap(ArkSwapType.ChainArkToEvm, ArkSwapStatus.Settled);
        Assert.That(Classify(swap, SwapExpired), Is.Null);
    }

    // ── Refunding our own EVM lockup (ChainEvmToArk, swap expired before Boltz locked Ark) ─

    [Test]
    public void ChainEvmToArk_SwapExpired_ReturnsCanRefundEvmLockup()
    {
        var swap = MakeSwap(ArkSwapType.ChainEvmToArk);
        Assert.That(Classify(swap, SwapExpired), Is.EqualTo(EvmSwapAction.CanRefundEvmLockup));
    }

    [Test]
    public void ChainEvmToArk_SwapExpired_AlreadyRefunded_ReturnsNull()
    {
        var swap = MakeSwap(ArkSwapType.ChainEvmToArk, ArkSwapStatus.Refunded);
        Assert.That(Classify(swap, SwapExpired), Is.Null);
    }

    [Test]
    public void ChainEvmToArk_SwapExpired_AlreadySettled_ReturnsNull()
    {
        var swap = MakeSwap(ArkSwapType.ChainEvmToArk, ArkSwapStatus.Settled);
        Assert.That(Classify(swap, SwapExpired), Is.Null);
    }

    // ── Refund/claim directions don't cross-trigger on the wrong swap type ──────────

    [Test]
    public void ChainEvmToArk_SwapExpired_DoesNotReturnCanRefundArkLockup()
    {
        var swap = MakeSwap(ArkSwapType.ChainEvmToArk);
        Assert.That(Classify(swap, SwapExpired), Is.Not.EqualTo(EvmSwapAction.CanRefundArkLockup));
    }

    [Test]
    public void ChainArkToEvm_SwapExpired_DoesNotReturnCanRefundEvmLockup()
    {
        var swap = MakeSwap(ArkSwapType.ChainArkToEvm);
        Assert.That(Classify(swap, SwapExpired), Is.Not.EqualTo(EvmSwapAction.CanRefundEvmLockup));
    }

    // ── Renegotiation (lockup amount mismatch, either direction) ────────────────────

    [TestCase(ArkSwapType.ChainArkToEvm)]
    [TestCase(ArkSwapType.ChainEvmToArk)]
    public void Chain_LockupFailed_ReturnsCanRenegotiateChain(ArkSwapType type)
    {
        var swap = MakeSwap(type);
        Assert.That(Classify(swap, TransactionLockupFailed), Is.EqualTo(EvmSwapAction.CanRenegotiateChain));
    }

    [Test]
    public void ChainArkToEvm_LockupFailed_AlreadySettled_ReturnsNull()
    {
        var swap = MakeSwap(ArkSwapType.ChainArkToEvm, ArkSwapStatus.Settled);
        Assert.That(Classify(swap, TransactionLockupFailed), Is.Null);
    }

    // ── Non-EVM swap types never produce an EVM action ──────────────────────────────

    [TestCase(TransactionServerMempool)]
    [TestCase(TransactionServerConfirmed)]
    [TestCase(SwapExpired)]
    public void NonEvmSwapTypes_NeverReturnEvmAction(string boltzStatus)
    {
        foreach (var type in new[] { ArkSwapType.Submarine, ArkSwapType.ChainArkToBtc, ArkSwapType.ChainBtcToArk })
        {
            var swap = MakeSwap(type);
            Assert.That(Classify(swap, boltzStatus), Is.Null,
                $"{type} + {boltzStatus} should never produce an EVM action");
        }
    }

    // ── Terminal swaps — no action regardless of Boltz status ───────────────────────

    [TestCase(SwapExpired)]
    [TestCase(TransactionServerMempool)]
    [TestCase(TransactionServerConfirmed)]
    public void AnyEvmType_AlreadySettled_ReturnsNull(string boltzStatus)
    {
        foreach (var type in new[] { ArkSwapType.ChainArkToEvm, ArkSwapType.ChainEvmToArk })
        {
            var swap = MakeSwap(type, ArkSwapStatus.Settled);
            Assert.That(Classify(swap, boltzStatus), Is.Null,
                $"{type} + {boltzStatus} should be null when already Settled");
        }
    }

    // ── Non-actionable statuses return null ──────────────────────────────────────────

    [TestCase(SwapCreated)]
    [TestCase(TransactionMempool)]
    [TestCase(TransactionConfirmed)]
    [TestCase(TransactionClaimPending)]
    [TestCase(TransactionClaimed)]
    [TestCase(TransactionRefunded)]
    public void AnyEvmType_NonActionableStatus_ReturnsNull(string boltzStatus)
    {
        foreach (var type in new[] { ArkSwapType.ChainArkToEvm, ArkSwapType.ChainEvmToArk })
        {
            var swap = MakeSwap(type);
            Assert.That(Classify(swap, boltzStatus), Is.Null,
                $"{type} + {boltzStatus} should not trigger any action");
        }
    }
}
