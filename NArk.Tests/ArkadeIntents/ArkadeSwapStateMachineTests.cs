using NArk.Abstractions.VTXOs;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Services;

namespace NArk.Tests.ArkadeIntents;

/// <summary>
/// Every transition an Arkade intent swap can make, in one place.
/// </summary>
/// <remarks>
/// The machine reads the state a swap is IN as well as what the chain says, and that is the point.
/// The same observation means opposite things depending on where we are and which corridor we are
/// on: a spend is a fill, or our own cancel landing, or an ambiguous outcome, depending. A rule that
/// looked only at the chain had to have those distinctions bolted on elsewhere, which is where they
/// went missing.
/// </remarks>
[TestFixture]
public class ArkadeSwapStateMachineTests
{
    private const long Locktime = 1_800_000_000;
    private const long Before = Locktime - 3600;
    private const long After = Locktime + 1;

    // ─── Send: arkade → lightning ─────────────────────────────────────

    [Test]
    public void Send_SpentBeforeTheLocktime_CanOnlyBeTheFill()
    {
        // The refund leaf is not spendable yet, so nothing else could have moved it.
        Assert.That(Next(Send, Pending, Spent(Before)), Is.EqualTo(ArkadeSwapIntentStatus.Fulfilled));
    }

    [Test]
    public void Send_SpentAtOrAfterTheLocktime_IsReportedRatherThanGuessed()
    {
        // Both the claim and the refund are live; telling them apart needs the spending witness.
        Assert.That(Next(Send, Refundable, Spent(After)), Is.EqualTo(ArkadeSwapIntentStatus.Resolved));
    }

    [Test]
    public void Send_UnspentPastTheLocktime_BecomesRefundable()
    {
        Assert.That(Next(Send, Pending, Open(After)), Is.EqualTo(ArkadeSwapIntentStatus.Refundable));
    }

    [Test]
    public void Send_UnspentBeforeTheLocktime_WaitsOnTheSolver()
    {
        Assert.That(Next(Send, Pending, Open(Before)), Is.Null);
    }

    // ─── Receive: lightning → arkade ──────────────────────────────────

    [Test]
    public void Receive_FundedAndUnspent_IsOursToClaim()
    {
        // The event the send leg has no equivalent of: the counterparty paid out first.
        Assert.That(Next(Receive, Pending, Open(Before)), Is.EqualTo(ArkadeSwapIntentStatus.Claimable));
    }

    [Test]
    public void Receive_UnspentPastTheDeadline_StopsBeingClaimable()
    {
        // The solver's own reclaim is open; calling it claimable would invite a spend racing it.
        Assert.That(Next(Receive, Claimable, Open(After)), Is.Null);
    }

    [Test]
    public void Receive_SpentBeforeTheDeadline_IsOurClaimLanding()
    {
        Assert.That(Next(Receive, Claimable, Spent(Before)), Is.EqualTo(ArkadeSwapIntentStatus.Fulfilled));
    }

    [Test]
    public void TheTwoLightningCorridors_ReadAnUnspentLockupOppositely()
    {
        // The distinction the whole type exists for, asserted directly.
        Assert.Multiple(() =>
        {
            Assert.That(Next(Send, Pending, Open(Before)), Is.Null, "send: waiting on the solver");
            Assert.That(Next(Receive, Pending, Open(Before)), Is.EqualTo(ArkadeSwapIntentStatus.Claimable),
                "receive: ours to move, on a clock");
        });
    }

    // ─── Asset corridors ──────────────────────────────────────────────

    [Test]
    public void Asset_Spent_IsTheFill()
    {
        // No refund leaf here, so a spend is unambiguous whatever the clock says.
        Assert.That(Next(Asset, Pending, Spent(After)), Is.EqualTo(ArkadeSwapIntentStatus.Fulfilled));
    }

    [Test]
    public void Asset_Open_StaysPending()
    {
        Assert.That(Next(Asset, Pending, Open(Before)), Is.Null);
    }

    // ─── Guards that need the current state ───────────────────────────

    [Test]
    public void ASpendWhileCancelling_IsOurOwnCancel()
    {
        // Reading this as a fill would credit the counterparty with something it never did.
        Assert.That(Next(Asset, ArkadeSwapIntentStatus.Cancelling, Spent(Before)),
            Is.EqualTo(ArkadeSwapIntentStatus.Cancelled));
    }

    [Test]
    public void WhileCancelling_NothingElseMoves()
    {
        Assert.That(Next(Asset, ArkadeSwapIntentStatus.Cancelling, Open(Before)), Is.Null);
    }

    [TestCase(ArkadeSwapIntentStatus.Fulfilled)]
    [TestCase(ArkadeSwapIntentStatus.Cancelled)]
    [TestCase(ArkadeSwapIntentStatus.Resolved)]
    [TestCase(ArkadeSwapIntentStatus.Recoverable)]
    public void ATerminalSwap_NeverReopens(ArkadeSwapIntentStatus terminal)
    {
        // Without this a swept-then-spent output could walk a finished row backwards.
        Assert.Multiple(() =>
        {
            Assert.That(Next(Send, terminal, Spent(After)), Is.Null);
            Assert.That(Next(Receive, terminal, Open(Before)), Is.Null);
            Assert.That(Next(Asset, terminal, Swept(Before)), Is.Null);
        });
    }

    [Test]
    public void AStatusIsNotRepeated()
    {
        // The monitor writes on change only; re-announcing the state we are in would be churn.
        Assert.That(Next(Receive, Claimable, Open(Before)), Is.Null);
        Assert.That(Next(Send, Refundable, Open(After)), Is.Null);
    }

    [Test]
    public void ASweptLockup_IsRecoverable()
    {
        Assert.That(Next(Send, Pending, Swept(Before)), Is.EqualTo(ArkadeSwapIntentStatus.Recoverable));
    }

    // ─── What we do about a state ─────────────────────────────────────

    [Test]
    public void OnlyConsequencesAreAutomated()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ArkadeSwapStateMachine.ActionFor(Receive, Claimable),
                Is.EqualTo(ArkadeIntentAction.ClaimReceive));
            Assert.That(ArkadeSwapStateMachine.ActionFor(Send, Refundable),
                Is.EqualTo(ArkadeIntentAction.RefundSend));

            // A pending asset swap is waiting to be filled, which is what was asked for.
            Assert.That(ArkadeSwapStateMachine.ActionFor(Asset, Pending), Is.EqualTo(ArkadeIntentAction.None));
            // Claimable is only meaningful on the receive leg.
            Assert.That(ArkadeSwapStateMachine.ActionFor(Send, Claimable), Is.EqualTo(ArkadeIntentAction.None));
        });
    }

    // ─── The documented steps ─────────────────────────────────────────

    [TestCase(ArkadeSwapIntentType.BtcToLightning)]
    [TestCase(ArkadeSwapIntentType.LightningToBtc)]
    [TestCase(ArkadeSwapIntentType.BtcToAsset)]
    public void TheStepsAreOrderedAndLandInReachableStates(ArkadeSwapIntentType type)
    {
        var steps = ArkadeSwapStateMachine.Steps(type);

        Assert.Multiple(() =>
        {
            Assert.That(steps, Is.Not.Empty);
            Assert.That(steps.Select(s => s.Ordinal), Is.EqualTo(Enumerable.Range(1, steps.Count)));
            // A step claiming to leave the swap somewhere the machine never reaches would be
            // documentation quietly drifting from behaviour.
            foreach (var landing in steps.Where(s => s.Leaves is not null).Select(s => s.Leaves!.Value))
            {
                Assert.That(Reachable(type), Does.Contain(landing), $"{type} can reach {landing}");
            }
        });
    }

    /// <summary>Every status the machine can actually produce for a corridor, plus its entry state.</summary>
    private static IReadOnlyCollection<ArkadeSwapIntentStatus> Reachable(ArkadeSwapIntentType type)
    {
        var seen = new HashSet<ArkadeSwapIntentStatus> { Pending, ArkadeSwapIntentStatus.Cancelling };
        var observations = new[] { Open(Before), Open(After), Spent(Before), Spent(After), Swept(Before) };

        for (var settled = false; !settled;)
        {
            settled = true;
            foreach (var from in seen.ToArray())
            foreach (var o in observations)
            {
                if (ArkadeSwapStateMachine.Next(type, from, o) is { } to && seen.Add(to)) settled = false;
            }
        }

        return seen;
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private const ArkadeSwapIntentType Send = ArkadeSwapIntentType.BtcToLightning;
    private const ArkadeSwapIntentType Receive = ArkadeSwapIntentType.LightningToBtc;
    private const ArkadeSwapIntentType Asset = ArkadeSwapIntentType.BtcToAsset;

    private const ArkadeSwapIntentStatus Pending = ArkadeSwapIntentStatus.Pending;
    private const ArkadeSwapIntentStatus Claimable = ArkadeSwapIntentStatus.Claimable;
    private const ArkadeSwapIntentStatus Refundable = ArkadeSwapIntentStatus.Refundable;

    private static ArkadeSwapIntentStatus? Next(
        ArkadeSwapIntentType type, ArkadeSwapIntentStatus current, SwapObservation o) =>
        ArkadeSwapStateMachine.Next(type, current, o);

    private static SwapObservation Open(long now) => new(Spent: false, Swept: false, now, Locktime);
    private static SwapObservation Spent(long now) => new(Spent: true, Swept: false, now, Locktime);
    private static SwapObservation Swept(long now) => new(Spent: false, Swept: true, now, Locktime);

    [Test]
    public void AnObservationReadsOffAVtxo()
    {
        var vtxo = new ArkVtxo(
            Script: "5120aa", TransactionId: "tx", TransactionOutputIndex: 0, Amount: 50_000,
            SpentByTransactionId: "spender", SettledByTransactionId: null, Swept: false,
            CreatedAt: DateTimeOffset.UtcNow, ExpiresAt: null, ExpiresAtHeight: null, ArkTxid: null);

        var observation = SwapObservation.From(vtxo, After, Locktime);

        Assert.Multiple(() =>
        {
            Assert.That(observation.Spent, Is.True);
            Assert.That(observation.Swept, Is.False);
            Assert.That(observation.PastLocktime, Is.True);
        });
    }
}
