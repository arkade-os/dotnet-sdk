namespace NArk.Swaps.Evm;

/// <summary>Why a chain swap's timeouts were rejected.</summary>
public enum SwapTimeoutViolation
{
    /// <summary>The timeouts satisfy every checked invariant.</summary>
    None,

    /// <summary>
    /// The leg we have to claim expires too soon (or has already expired) for us to realistically
    /// claim it.
    /// </summary>
    ClaimWindowTooShort,

    /// <summary>
    /// Our own lockup becomes refundable before — or too soon after — the leg we claim expires.
    /// </summary>
    RefundNotAfterClaimDeadline,
}

/// <summary>
/// Validates the cross-chain timelock ordering of a chain swap before any funds are committed.
/// Pure, so the rules stay reviewable and testable without a chain — mirrors
/// <see cref="EvmChainOperationClassifier"/> and <see cref="EvmIdempotencyResolver"/>.
/// </summary>
/// <remarks>
/// <para>
/// A chain swap is only safe if the leg <em>we</em> claim expires meaningfully <em>before</em> our
/// own lockup becomes refundable. That ordering is what makes the swap atomic: we claim their leg,
/// which publishes the preimage, and they still have time to claim ours with it. Reverse the
/// ordering and there is a window where our funds are refundable to us while their leg is still
/// live — the counterparty's side of the trade stops being safe, and a counterparty that notices
/// simply refuses to participate, leaving us to discover the problem only when funds are stuck.
/// </para>
/// <para>
/// Boltz derives these deltas server-side (<c>TimeoutDeltaProvider.convertBlocks(sending,
/// receiving, sendingTimeoutBlockDelta * 1.5)</c>), so the values it returns should always pass.
/// Checking anyway is the point: nothing about the ordering is enforced by the contracts or the
/// protocol, so a server bug — or a server that stops behaving — would otherwise be discovered
/// only after the funds are locked.
/// </para>
/// <para>
/// Both deadlines arrive as wall-clock instants because the two legs count time in different
/// units (Arkade in Bitcoin blocks or a timestamp, the EVM leg in its own chain's blocks). The
/// conversion from block heights is the caller's job and is necessarily approximate — see
/// <see cref="EvmSwapOptions.EvmBlockTime"/> and <see cref="EvmSwapOptions.ArkadeBlockTime"/>.
/// The margins below exist to absorb that approximation.
/// </para>
/// </remarks>
public static class EvmSwapTimeoutValidator
{
    /// <summary>
    /// Checks a swap's two deadlines.
    /// </summary>
    /// <param name="now">Current wall-clock time.</param>
    /// <param name="claimDeadline">
    /// When the leg we must claim stops being claimable — i.e. when the counterparty can refund it.
    /// </param>
    /// <param name="ourRefundAvailableAt">When our own lockup becomes refundable to us.</param>
    /// <param name="minClaimWindow">How much time we insist on having to perform the claim.</param>
    /// <param name="minOrderingMargin">
    /// How far after <paramref name="claimDeadline"/> our refund must become available. Absorbs
    /// block-time estimation error on both chains.
    /// </param>
    public static SwapTimeoutViolation Validate(
        DateTimeOffset now,
        DateTimeOffset claimDeadline,
        DateTimeOffset ourRefundAvailableAt,
        TimeSpan minClaimWindow,
        TimeSpan minOrderingMargin)
    {
        if (claimDeadline - now < minClaimWindow)
            return SwapTimeoutViolation.ClaimWindowTooShort;

        if (ourRefundAvailableAt - claimDeadline < minOrderingMargin)
            return SwapTimeoutViolation.RefundNotAfterClaimDeadline;

        return SwapTimeoutViolation.None;
    }

    /// <summary>
    /// Converts an absolute target block on a chain into a wall-clock deadline, given the chain's
    /// current height and its average block time. A target at or below
    /// <paramref name="currentBlock"/> yields <paramref name="now"/> — already expired.
    /// </summary>
    public static DateTimeOffset BlockToDeadline(
        DateTimeOffset now, long currentBlock, long targetBlock, TimeSpan blockTime)
    {
        if (targetBlock <= currentBlock) return now;

        // Guard the multiplication: an absurd target (a parse error, a wrong-units value) would
        // otherwise overflow into a deadline far enough out that every check trivially passes.
        var blocks = targetBlock - currentBlock;
        var ticks = blockTime.Ticks * (decimal)blocks;
        return ticks > DateTimeOffset.MaxValue.Ticks - now.Ticks
            ? DateTimeOffset.MaxValue
            : now + TimeSpan.FromTicks((long)ticks);
    }

    /// <summary>Human-readable explanation for a violation, for exception and log messages.</summary>
    public static string Describe(
        SwapTimeoutViolation violation, DateTimeOffset now,
        DateTimeOffset claimDeadline, DateTimeOffset ourRefundAvailableAt) =>
        violation switch
        {
            SwapTimeoutViolation.ClaimWindowTooShort =>
                $"the leg we must claim expires at {claimDeadline:u}, only {claimDeadline - now:g} from now — " +
                "too little time to claim it safely",
            SwapTimeoutViolation.RefundNotAfterClaimDeadline =>
                $"our lockup becomes refundable at {ourRefundAvailableAt:u}, which is not safely after the " +
                $"{claimDeadline:u} deadline for claiming the other leg — the swap would not be atomic",
            _ => "timeouts are valid",
        };
}
