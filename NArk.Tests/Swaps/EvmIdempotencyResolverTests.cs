using NArk.Swaps.Evm;
using NUnit.Framework;
using static NArk.Swaps.Evm.EvmIdempotencyResolver;

namespace NArk.Tests.Swaps;

/// <summary>
/// Unit tests for <see cref="EvmIdempotencyResolver"/> — the decision table guarding
/// <c>ERC20Swap</c> lock/claim/refund against duplicate broadcasts, mirroring
/// <see cref="EvmChainOperationClassifierTests"/>'s table-driven shape.
/// </summary>
/// <remarks>
/// The cases that matter are the ones where the two signals disagree. An on-chain event with no
/// recorded hash is a swap recovered from storage after a restart; a recorded hash with no event
/// is a transaction still in flight — and that second case is the one a naive "just check the
/// chain" guard gets wrong, re-broadcasting a lock the contract reverts as a duplicate preimage
/// hash and turning a successful lock into a hard failure.
/// </remarks>
[TestFixture]
public class EvmIdempotencyResolverTests
{
    private const string TxHash = "0xabc123";

    // ── Lock ────────────────────────────────────────────────────────────────

    [Test]
    public void Lock_NothingCommitted_Broadcasts() =>
        Assert.That(ResolveLock(lockupOnChain: false, recordedLockTxId: null), Is.EqualTo(EvmTxAction.Broadcast));

    [Test]
    public void Lock_EventOnChain_IsAlreadyDone() =>
        Assert.That(ResolveLock(lockupOnChain: true, recordedLockTxId: null), Is.EqualTo(EvmTxAction.AlreadyDone));

    [Test]
    public void Lock_BroadcastButNotYetIndexed_WaitsInsteadOfReBroadcasting() =>
        Assert.That(ResolveLock(lockupOnChain: false, recordedLockTxId: TxHash), Is.EqualTo(EvmTxAction.AwaitBroadcast));

    [Test]
    public void Lock_EventOnChainWinsOverRecordedHash() =>
        Assert.That(ResolveLock(lockupOnChain: true, recordedLockTxId: TxHash), Is.EqualTo(EvmTxAction.AlreadyDone));

    [TestCase("")]
    [TestCase(null)]
    public void Lock_BlankRecordedHashCountsAsNoBroadcast(string? recorded) =>
        Assert.That(ResolveLock(lockupOnChain: false, recorded), Is.EqualTo(EvmTxAction.Broadcast));

    // ── Claim ───────────────────────────────────────────────────────────────

    [Test]
    public void Claim_NothingCommitted_Broadcasts() =>
        Assert.That(ResolveClaim(claimOnChain: false, recordedClaimTxId: null), Is.EqualTo(EvmTxAction.Broadcast));

    [Test]
    public void Claim_EventOnChain_IsAlreadyDone() =>
        Assert.That(ResolveClaim(claimOnChain: true, recordedClaimTxId: null), Is.EqualTo(EvmTxAction.AlreadyDone));

    [Test]
    public void Claim_BroadcastButNotYetIndexed_Waits() =>
        Assert.That(ResolveClaim(claimOnChain: false, recordedClaimTxId: TxHash), Is.EqualTo(EvmTxAction.AwaitBroadcast));

    // ── Refund ──────────────────────────────────────────────────────────────

    [Test]
    public void Refund_LockupPresent_Broadcasts() =>
        Assert.That(
            ResolveRefund(refundOnChain: false, recordedRefundTxId: null, lockupOnChain: true, recordedLockTxId: TxHash),
            Is.EqualTo(EvmRefundAction.Broadcast));

    [Test]
    public void Refund_AlreadyRefundedOnChain_IsTerminal() =>
        Assert.That(
            ResolveRefund(refundOnChain: true, recordedRefundTxId: null, lockupOnChain: true, recordedLockTxId: TxHash),
            Is.EqualTo(EvmRefundAction.AlreadyRefunded));

    [Test]
    public void Refund_BroadcastButNotYetIndexed_Waits() =>
        Assert.That(
            ResolveRefund(refundOnChain: false, recordedRefundTxId: TxHash, lockupOnChain: true, recordedLockTxId: TxHash),
            Is.EqualTo(EvmRefundAction.AwaitBroadcast));

    /// <summary>
    /// The case this whole guard exists for. No lockup is visible, but we did broadcast a lock —
    /// so funds are most likely committed and merely un-indexed. Failing the swap here would
    /// drop it out of the poll loop, and the poll loop is the only thing that would ever refund
    /// those funds.
    /// </summary>
    [Test]
    public void Refund_NoLockupButLockWasBroadcast_KeepsSwapActive() =>
        Assert.That(
            ResolveRefund(refundOnChain: false, recordedRefundTxId: null, lockupOnChain: false, recordedLockTxId: TxHash),
            Is.EqualTo(EvmRefundAction.WaitForLockup));

    [Test]
    public void Refund_NoLockupAndNoLockBroadcast_IsGenuinelyEmpty() =>
        Assert.That(
            ResolveRefund(refundOnChain: false, recordedRefundTxId: null, lockupOnChain: false, recordedLockTxId: null),
            Is.EqualTo(EvmRefundAction.NothingLocked));

    [TestCase("")]
    [TestCase(null)]
    public void Refund_BlankLockHashIsNotTreatedAsABroadcast(string? recordedLock) =>
        Assert.That(
            ResolveRefund(refundOnChain: false, recordedRefundTxId: null, lockupOnChain: false, recordedLock),
            Is.EqualTo(EvmRefundAction.NothingLocked));

    /// <summary>
    /// Precedence check across all four inputs at once: a refund already on-chain outranks
    /// every other signal, so a stale lock hash can never resurrect a settled refund.
    /// </summary>
    [Test]
    public void Refund_RefundEventOutranksEverything() =>
        Assert.That(
            ResolveRefund(refundOnChain: true, recordedRefundTxId: TxHash, lockupOnChain: true, recordedLockTxId: TxHash),
            Is.EqualTo(EvmRefundAction.AlreadyRefunded));
}
