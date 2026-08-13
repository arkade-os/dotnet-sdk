using NArk.ArkadeIntents.Models;

namespace NArk.ArkadeIntents.Services;

/// <summary>What a swap needs done to it next, if anything.</summary>
public enum ArkadeIntentAction
{
    /// <summary>Nothing to do — it is waiting on someone else, or it is over.</summary>
    None,

    /// <summary>The counterparty funded a receive swap; spend the claim before its window closes.</summary>
    ClaimReceive,

    /// <summary>A send swap's refund path opened and nobody else will push it.</summary>
    RefundSend,
}

/// <summary>
/// Decides the next move for a swap from its kind and status alone.
/// </summary>
/// <remarks>
/// <para>
/// Kept as a pure function so the decision is testable at its exact boundary, the same way the
/// corridors' funding gates are. It answers only one question — "would a caller with no further
/// information be right to act now?" — and the bar for yes is deliberately high.
/// </para>
/// <para>
/// The line it draws is between a consequence and a decision. Claiming a funded receive swap and
/// refunding a send swap past its locktime are consequences: the money is already ours, the window
/// is finite, and nobody else is coming to do it. Cancelling a pending asset swap is a decision —
/// it is still waiting to be filled, which is exactly what was asked for, and an automation that
/// cancelled it would destroy the thing it was meant to look after.
/// </para>
/// </remarks>
public static class ArkadeIntentPolicy
{
    /// <summary>The action this swap calls for right now.</summary>
    /// <param name="intent">The swap.</param>
    /// <returns>What to do, or <see cref="ArkadeIntentAction.None"/>.</returns>
    public static ArkadeIntentAction NextAction(ArkadeSwapIntent intent) =>
        ArkadeSwapStateMachine.ActionFor(intent.Type, intent.Status);
}
