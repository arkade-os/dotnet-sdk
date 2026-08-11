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

/// <summary>What makes a step happen.</summary>
/// <remarks>
/// Deliberately not "who does it". Waiting out a locktime has no doer — nobody acts, a condition
/// simply becomes true — and forcing that into an actor means inventing an agent that does not
/// exist. Asking what causes a step instead covers both cases honestly, and it is the question a
/// caller actually has: is this mine to do, am I waiting on someone, or am I waiting on nothing but
/// time?
/// </remarks>
public enum SwapTrigger
{
    /// <summary>We do it.</summary>
    Client,

    /// <summary>The counterparty does it, and we find out by watching.</summary>
    Solver,

    /// <summary>Nobody does it; a deadline passes.</summary>
    Time,
}

/// <summary>What happens at a step, as something a program can branch on.</summary>
/// <remarks>
/// Named rather than described. A free-text step is prose sitting in a type: nothing can consume it,
/// nothing stops it drifting, and a caller that wanted to react to a particular step would be
/// matching on English. These are the distinct things that actually happen, so a UI can label them,
/// a log can key on them, and a new one cannot be invented by typing a sentence.
/// </remarks>
public enum SwapStepKind
{
    /// <summary>Choose the preimage and seal it to covclaimd.</summary>
    SealPreimage,

    /// <summary>Ask a solver for terms.</summary>
    Negotiate,

    /// <summary>Check what the quote sent back against what was asked for.</summary>
    VerifyQuote,

    /// <summary>Derive the covenant locally and refuse on any mismatch.</summary>
    DeriveAndVerifyAddress,

    /// <summary>Apply the safety gates immediately before committing.</summary>
    Gate,

    /// <summary>Import the contract so the funded script can be rebuilt later.</summary>
    ImportContract,

    /// <summary>Fund the lockup.</summary>
    FundLockup,

    /// <summary>Hand the minted invoice to whoever is paying.</summary>
    PublishInvoice,

    /// <summary>The counterparty funds its side.</summary>
    CounterpartyFunds,

    /// <summary>The counterparty fills — pays the invoice, or takes the offer.</summary>
    CounterpartyFills,

    /// <summary>The counterparty claims the lockup with the preimage.</summary>
    CounterpartyClaims,

    /// <summary>Wait out the refund locktime.</summary>
    AwaitRefundLocktime,

    /// <summary>Spend the claim, publishing the preimage.</summary>
    Claim,

    /// <summary>Spend the refund back to our own address.</summary>
    Refund,

    /// <summary>Take the deposit back by choice.</summary>
    Cancel,
}

/// <summary>One step in a corridor's lifecycle.</summary>
/// <param name="Ordinal">Its position, from 1.</param>
/// <param name="Kind">What happens.</param>
/// <param name="Trigger">What makes it happen.</param>
/// <param name="Leaves">The status the swap sits in once this step is done, when it moves it.</param>
public sealed record SwapStep(
    int Ordinal, SwapStepKind Kind, SwapTrigger Trigger, ArkadeSwapIntentStatus? Leaves);

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

            // Seeing the lockup at all is proof the funding spend landed, which is the only
            // confirmation a swap recorded before its own spend ever gets. From Pending this is a
            // no-op; from Funding it is the promotion.
            ArkadeSwapIntentType.BtcToLightning =>
                Changed(current, ArkadeSwapIntentStatus.Pending),

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
    /// <para>
    /// Only consequences, never decisions. Claiming a funded receive swap and refunding a send swap
    /// past its locktime both follow from the state with nothing left to weigh — the money is
    /// already ours, the window is finite, and no one else is coming. Cancelling a pending asset
    /// swap is a choice: it is still waiting to be filled, which is what was asked for.
    /// </para>
    /// <para>
    /// Both are also callable directly, and this is only the sweep's opinion about them. Neither
    /// needs a counterparty, which is what makes automating them safe at all — the covenant's other
    /// refund leaves need the solver's signature and the protocol has no way to ask for one, so
    /// there is nothing for a timer to attempt there even in principle.
    /// </para>
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
    /// The ordered steps of a corridor: what happens, who does it, and where it leaves the swap.
    /// </summary>
    /// <remarks>
    /// Not every step moves the swap — negotiating and verifying leave no trace in storage, and are
    /// listed anyway because the sequence is the thing a caller needs to reason about. The ones that
    /// do move it are checked against the transition table by test, so this cannot describe a state
    /// the machine never reaches.
    /// </remarks>
    /// <param name="type">Which corridor.</param>
    /// <returns>The steps, in order.</returns>
    public static IReadOnlyList<SwapStep> Steps(ArkadeSwapIntentType type) => type switch
    {
        ArkadeSwapIntentType.BtcToLightning =>
        [
            new(1, SwapStepKind.Negotiate, SwapTrigger.Client, null),
            new(2, SwapStepKind.DeriveAndVerifyAddress, SwapTrigger.Client, null),
            new(3, SwapStepKind.Gate, SwapTrigger.Client, null),
            new(4, SwapStepKind.ImportContract, SwapTrigger.Client, ArkadeSwapIntentStatus.Funding),
            new(5, SwapStepKind.FundLockup, SwapTrigger.Client, ArkadeSwapIntentStatus.Pending),
            new(6, SwapStepKind.CounterpartyFills, SwapTrigger.Solver, null),
            new(7, SwapStepKind.CounterpartyClaims, SwapTrigger.Solver, ArkadeSwapIntentStatus.Fulfilled),
            new(8, SwapStepKind.AwaitRefundLocktime, SwapTrigger.Time, ArkadeSwapIntentStatus.Refundable),
            new(9, SwapStepKind.Refund, SwapTrigger.Client, ArkadeSwapIntentStatus.Cancelled),
        ],

        ArkadeSwapIntentType.LightningToBtc =>
        [
            new(1, SwapStepKind.SealPreimage, SwapTrigger.Client, null),
            new(2, SwapStepKind.Negotiate, SwapTrigger.Client, null),
            new(3, SwapStepKind.VerifyQuote, SwapTrigger.Client, null),
            new(4, SwapStepKind.DeriveAndVerifyAddress, SwapTrigger.Client, null),
            new(5, SwapStepKind.ImportContract, SwapTrigger.Client, ArkadeSwapIntentStatus.Pending),
            new(6, SwapStepKind.PublishInvoice, SwapTrigger.Client, null),
            new(7, SwapStepKind.CounterpartyFunds, SwapTrigger.Solver, ArkadeSwapIntentStatus.Claimable),
            new(8, SwapStepKind.Claim, SwapTrigger.Client, ArkadeSwapIntentStatus.Fulfilled),
        ],

        _ =>
        [
            new(1, SwapStepKind.FundLockup, SwapTrigger.Client, ArkadeSwapIntentStatus.Pending),
            new(2, SwapStepKind.CounterpartyFills, SwapTrigger.Solver, ArkadeSwapIntentStatus.Fulfilled),
            new(3, SwapStepKind.Cancel, SwapTrigger.Client, ArkadeSwapIntentStatus.Cancelled),
        ],
    };
}
