using NArk.Abstractions.VTXOs;
using NArk.ArkadeIntents.Models;

namespace NArk.ArkadeIntents.Services;

/// <summary>What the chain says about a swap's lockup at one moment.</summary>
/// <param name="Spent">The lockup output has been spent.</param>
/// <param name="Swept">The lockup output expired and was swept.</param>
/// <param name="Now">Unix seconds.</param>
/// <param name="RefundLocktime">
/// Unix seconds at which the covenant's timelocked path opens, when the corridor has one.
/// </param>
public readonly record struct SwapObservation(bool Spent, bool Swept, long Now, long? RefundLocktime)
{
    /// <summary>Read an observation off a VTXO.</summary>
    /// <param name="vtxo">The lockup output.</param>
    /// <param name="now">Unix seconds.</param>
    /// <param name="refundLocktime">The corridor's refund locktime, if it has one.</param>
    /// <returns>The observation.</returns>
    public static SwapObservation From(ArkVtxo vtxo, long now, long? refundLocktime) =>
        new(vtxo.IsSpent(), vtxo.Swept, now, refundLocktime);

    /// <summary>True once the timelocked path is open.</summary>
    public bool PastLocktime => RefundLocktime is { } t && Now >= t;
}

/// <summary>Who or what moves a swap from one state to the next.</summary>
public enum SwapActor
{
    /// <summary>Us.</summary>
    Client,

    /// <summary>The counterparty quoting and filling.</summary>
    Solver,

    /// <summary>The chain, observed rather than asked.</summary>
    Chain,

    /// <summary>Time passing.</summary>
    Clock,
}

/// <summary>One step in a corridor's lifecycle.</summary>
/// <param name="Ordinal">Its position, from 1.</param>
/// <param name="Name">What happens.</param>
/// <param name="Actor">Who makes it happen.</param>
/// <param name="Leaves">The status the swap sits in once this step is done.</param>
public sealed record SwapStep(int Ordinal, string Name, SwapActor Actor, ArkadeSwapIntentStatus? Leaves);

/// <summary>
/// The lifecycle of every Arkade intent swap, in one place: which states exist per corridor, what
/// moves between them, and what we are expected to do in each.
/// </summary>
/// <remarks>
/// <para>
/// Transitions are guarded by the state we are IN, not only by what the chain says. That difference
/// matters: a spend observed while we are mid-cancel is our own cancel landing, and a spend observed
/// while waiting is the counterparty filling. A projection of chain state alone cannot tell those
/// apart, which is why the guard used to live somewhere else as a special case.
/// </para>
/// <para>
/// The same status names mean different things per corridor, because the roles invert.
/// <see cref="ArkadeSwapIntentStatus.Fulfilled"/> on a send swap is the solver spending our lockup;
/// on a receive swap it is us spending theirs. Encoding both here keeps that from being rediscovered
/// at each call site.
/// </para>
/// </remarks>
public static class ArkadeSwapStateMachine
{
    /// <summary>States a swap never leaves.</summary>
    public static readonly IReadOnlySet<ArkadeSwapIntentStatus> Terminal =
        new HashSet<ArkadeSwapIntentStatus>
        {
            ArkadeSwapIntentStatus.Fulfilled,
            ArkadeSwapIntentStatus.Cancelled,
            ArkadeSwapIntentStatus.Resolved,
            ArkadeSwapIntentStatus.Recoverable,
        };

    /// <summary>
    /// The next status for a swap, or <c>null</c> when nothing has changed.
    /// </summary>
    /// <param name="type">Which corridor.</param>
    /// <param name="current">Where the swap is now.</param>
    /// <param name="observation">What the chain says.</param>
    /// <returns>The status to move to, or <c>null</c> to stay put.</returns>
    public static ArkadeSwapIntentStatus? Next(
        ArkadeSwapIntentType type, ArkadeSwapIntentStatus current, SwapObservation observation)
    {
        // Nothing reopens a finished swap. Without this a swept-then-spent output could walk a
        // terminal row backwards.
        if (Terminal.Contains(current)) return null;

        // We are mid-spend on our own cancel, so the spend we are about to see is ours. Reading it
        // as a fill would credit the counterparty with something it never did.
        if (current == ArkadeSwapIntentStatus.Cancelling)
        {
            return observation.Spent ? ArkadeSwapIntentStatus.Cancelled : null;
        }

        if (observation.Spent) return SpentInto(type, observation);
        if (observation.Swept) return ArkadeSwapIntentStatus.Recoverable;

        return WaitingInto(type, current, observation);
    }

    /// <summary>What a spend of the lockup means, once we know it was not our own cancel.</summary>
    private static ArkadeSwapIntentStatus SpentInto(ArkadeSwapIntentType type, SwapObservation o) =>
        type switch
        {
            // Both Lightning corridors carry a refund leaf, so the same spend means different things
            // either side of its deadline. Before it, only one party could have moved and the swap
            // is done; at or after it, both paths are live and attributing the spend needs the
            // witness, so the outcome is reported rather than guessed.
            ArkadeSwapIntentType.BtcToLightning or ArkadeSwapIntentType.LightningToBtc =>
                o.PastLocktime ? ArkadeSwapIntentStatus.Resolved : ArkadeSwapIntentStatus.Fulfilled,

            // The asset corridors have no such leaf: a spend can only be the fill.
            _ => ArkadeSwapIntentStatus.Fulfilled,
        };

    /// <summary>What an unspent, unswept lockup means while we wait.</summary>
    private static ArkadeSwapIntentStatus? WaitingInto(
        ArkadeSwapIntentType type, ArkadeSwapIntentStatus current, SwapObservation o) =>
        type switch
        {
            // We funded and are waiting on the solver. Once the refund path opens the money is ours
            // to take back, and nobody else will do it.
            ArkadeSwapIntentType.BtcToLightning when o.PastLocktime =>
                Changed(current, ArkadeSwapIntentStatus.Refundable),

            // The solver funded and only our preimage moves it. An unspent lockup here is not a swap
            // waiting on someone else — it is money on a clock, ours until the solver's own reclaim
            // path opens. Past that we stop calling it claimable rather than race the reclaim.
            ArkadeSwapIntentType.LightningToBtc when !o.PastLocktime =>
                Changed(current, ArkadeSwapIntentStatus.Claimable),

            _ => null,
        };

    private static ArkadeSwapIntentStatus? Changed(
        ArkadeSwapIntentStatus current, ArkadeSwapIntentStatus next) => current == next ? null : next;

    /// <summary>
    /// What we should do about a swap sitting in this state, if anything.
    /// </summary>
    /// <param name="type">Which corridor.</param>
    /// <param name="status">Where the swap is.</param>
    /// <returns>The action, or <see cref="ArkadeIntentAction.None"/>.</returns>
    /// <remarks>
    /// Only consequences, never decisions. Claiming a funded receive swap and refunding a send swap
    /// past its locktime both follow from the state with nothing left to weigh — the money is
    /// already ours, the window is finite, and no one else is coming. Cancelling a pending asset
    /// swap is a choice: it is still waiting to be filled, which is what was asked for.
    /// </remarks>
    public static ArkadeIntentAction ActionFor(
        ArkadeSwapIntentType type, ArkadeSwapIntentStatus status) => (type, status) switch
    {
        (ArkadeSwapIntentType.LightningToBtc, ArkadeSwapIntentStatus.Claimable) =>
            ArkadeIntentAction.ClaimReceive,
        (ArkadeSwapIntentType.BtcToLightning, ArkadeSwapIntentStatus.Refundable) =>
            ArkadeIntentAction.RefundSend,
        _ => ArkadeIntentAction.None,
    };

    /// <summary>
    /// The ordered steps of a corridor, as documentation the code can be checked against.
    /// </summary>
    /// <param name="type">Which corridor.</param>
    /// <returns>The steps, in order.</returns>
    public static IReadOnlyList<SwapStep> Steps(ArkadeSwapIntentType type) => type switch
    {
        ArkadeSwapIntentType.BtcToLightning =>
        [
            new(1, "negotiate terms by RFQ", SwapActor.Client, null),
            new(2, "derive the covenant locally and refuse on any mismatch", SwapActor.Client, null),
            new(3, "gate on invoice, quote validity and refund headroom", SwapActor.Client, null),
            new(4, "import the contract, then fund the lockup", SwapActor.Client, ArkadeSwapIntentStatus.Pending),
            new(5, "observe the funding and pay the invoice", SwapActor.Solver, null),
            new(6, "claim with the preimage", SwapActor.Solver, ArkadeSwapIntentStatus.Fulfilled),
            new(7, "or, unfilled, wait out the refund locktime", SwapActor.Clock, ArkadeSwapIntentStatus.Refundable),
            new(8, "push the refund to our own address", SwapActor.Client, ArkadeSwapIntentStatus.Cancelled),
        ],

        ArkadeSwapIntentType.LightningToBtc =>
        [
            new(1, "choose the preimage and seal it to covclaimd", SwapActor.Client, null),
            new(2, "negotiate terms by RFQ", SwapActor.Client, null),
            new(3, "check the minted invoice against what was asked for", SwapActor.Client, null),
            new(4, "derive the covenant locally and refuse on any mismatch", SwapActor.Client, null),
            new(5, "import the contract and record the preimage", SwapActor.Client, ArkadeSwapIntentStatus.Pending),
            new(6, "hand the invoice to a payer", SwapActor.Client, null),
            new(7, "fund the lockup once the payment is held", SwapActor.Solver, ArkadeSwapIntentStatus.Claimable),
            new(8, "claim, publishing the preimage and settling the invoice", SwapActor.Client, ArkadeSwapIntentStatus.Fulfilled),
        ],

        _ =>
        [
            new(1, "build the offer and fund the covenant", SwapActor.Client, ArkadeSwapIntentStatus.Pending),
            new(2, "fill the offer", SwapActor.Solver, ArkadeSwapIntentStatus.Fulfilled),
            new(3, "or cancel it back, which is the caller's decision", SwapActor.Client, ArkadeSwapIntentStatus.Cancelled),
        ],
    };
}
