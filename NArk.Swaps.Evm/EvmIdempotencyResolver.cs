namespace NArk.Swaps.Evm;

/// <summary>
/// What to do about an <c>ERC20Swap</c> state-changing call before broadcasting one.
/// </summary>
public enum EvmTxAction
{
    /// <summary>The on-chain effect is already there — the operation is complete, do nothing.</summary>
    AlreadyDone,

    /// <summary>
    /// A transaction was broadcast but its effect isn't visible on-chain yet. Wait for that
    /// transaction's receipt rather than broadcasting a competing one.
    /// </summary>
    AwaitBroadcast,

    /// <summary>Nothing is committed — broadcast a fresh transaction.</summary>
    Broadcast,
}

/// <summary>
/// What to do about a <c>ChainEvmToArk</c> refund. Richer than <see cref="EvmTxAction"/> because
/// the refund path also has to decide whether a swap with no visible lockup is genuinely empty
/// (and can be failed) or merely un-indexed (and must stay active).
/// </summary>
public enum EvmRefundAction
{
    /// <summary>A <c>Refund</c> event is already on-chain.</summary>
    AlreadyRefunded,

    /// <summary>A refund transaction was broadcast but isn't indexed yet — wait for its receipt.</summary>
    AwaitBroadcast,

    /// <summary>A lockup exists and no refund has been broadcast — refund it.</summary>
    Broadcast,

    /// <summary>
    /// No <c>Lockup</c> event visible, but a lock transaction was broadcast — the lockup is
    /// most likely still un-indexed. Keep the swap active so a later poll can refund it.
    /// </summary>
    WaitForLockup,

    /// <summary>
    /// No lockup event and no lock transaction was ever broadcast — nothing was ever committed,
    /// so the swap can be failed without stranding funds.
    /// </summary>
    NothingLocked,
}

/// <summary>
/// Pure decision table for the idempotency guards around <c>ERC20Swap</c> lock/claim/refund.
/// Mirrors <see cref="EvmChainOperationClassifier"/>'s shape — no I/O, so the ordering rules
/// stay reviewable and testable without a chain, an RPC endpoint or a mocked client.
/// </summary>
/// <remarks>
/// The precedence is the whole point. Broadcast and receipt are separate steps, so a lost
/// receipt (RPC timeout, restart, dropped connection) leaves a transaction whose effect is real
/// but not yet observable. Both signals are therefore consulted, on-chain event first and
/// recorded transaction hash second:
/// <list type="bullet">
///   <item><description>the event is authoritative but lags;</description></item>
///   <item><description>the recorded hash covers exactly the window where the event hasn't
///   landed yet — which is the window in which a naive retry would broadcast a duplicate that
///   the contract reverts, turning a success into a failure.</description></item>
/// </list>
/// </remarks>
public static class EvmIdempotencyResolver
{
    /// <summary>
    /// Resolves whether to broadcast an <c>ERC20Swap.lock</c>.
    /// </summary>
    /// <param name="lockupOnChain">Whether a <c>Lockup</c> event exists for this preimage hash.</param>
    /// <param name="recordedLockTxId">Previously recorded lock transaction hash, if any.</param>
    public static EvmTxAction ResolveLock(bool lockupOnChain, string? recordedLockTxId) =>
        Resolve(lockupOnChain, recordedLockTxId);

    /// <summary>
    /// Resolves whether to broadcast an <c>ERC20Swap.claim</c>.
    /// </summary>
    /// <param name="claimOnChain">Whether a <c>Claim</c> event exists for this preimage hash.</param>
    /// <param name="recordedClaimTxId">Previously recorded claim transaction hash, if any.</param>
    public static EvmTxAction ResolveClaim(bool claimOnChain, string? recordedClaimTxId) =>
        Resolve(claimOnChain, recordedClaimTxId);

    /// <summary>
    /// Resolves what the refund path should do.
    /// </summary>
    /// <param name="refundOnChain">Whether a <c>Refund</c> event exists for this preimage hash.</param>
    /// <param name="recordedRefundTxId">Previously recorded refund transaction hash, if any.</param>
    /// <param name="lockupOnChain">Whether a <c>Lockup</c> event exists for this preimage hash.</param>
    /// <param name="recordedLockTxId">Previously recorded lock transaction hash, if any.</param>
    public static EvmRefundAction ResolveRefund(
        bool refundOnChain, string? recordedRefundTxId, bool lockupOnChain, string? recordedLockTxId)
    {
        if (refundOnChain) return EvmRefundAction.AlreadyRefunded;
        if (!string.IsNullOrEmpty(recordedRefundTxId)) return EvmRefundAction.AwaitBroadcast;
        if (lockupOnChain) return EvmRefundAction.Broadcast;

        // No lockup visible. A recorded lock tx means we committed funds that simply haven't
        // been indexed yet — failing the swap here would drop it out of the poll loop, and the
        // poll loop is the only thing that would ever refund those funds.
        return string.IsNullOrEmpty(recordedLockTxId)
            ? EvmRefundAction.NothingLocked
            : EvmRefundAction.WaitForLockup;
    }

    private static EvmTxAction Resolve(bool effectOnChain, string? recordedTxId)
    {
        if (effectOnChain) return EvmTxAction.AlreadyDone;
        return string.IsNullOrEmpty(recordedTxId) ? EvmTxAction.Broadcast : EvmTxAction.AwaitBroadcast;
    }
}
