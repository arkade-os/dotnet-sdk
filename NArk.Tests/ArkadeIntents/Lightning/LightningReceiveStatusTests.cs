using NArk.Abstractions.VTXOs;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Services;

namespace NArk.Tests.ArkadeIntents.Lightning;

/// <summary>
/// How a receive swap's lockup reads, which is not how a send swap's does.
/// </summary>
/// <remarks>
/// The same VTXO state means opposite things on the two corridors, because the party that funds
/// changes. On the send leg an unspent lockup is our money waiting on a solver; here it is the
/// solver's money waiting on us, and only our preimage can move it. Reading one as the other would
/// have the client sit still through exactly the window it needs to act in.
/// </remarks>
[TestFixture]
public class LightningReceiveStatusTests
{
    private const long Locktime = 1_800_000_000;

    [Test]
    public void FundedAndUnspent_IsOursToClaim()
    {
        // The event the send leg has no equivalent of: the counterparty paid out first.
        Assert.That(
            ArkadeSwapIntentMonitoringService.ResolveLightningReceiveStatus(Vtxo(), Locktime, Locktime - 3600),
            Is.EqualTo(ArkadeSwapIntentStatus.Claimable));
    }

    [Test]
    public void SpentBeforeTheDeadline_CanOnlyBeOurOwnClaim()
    {
        // Nothing else can spend it yet — the solver's reclaim path has not opened.
        Assert.That(
            ArkadeSwapIntentMonitoringService.ResolveLightningReceiveStatus(
                Vtxo(spentBy: "tx"), Locktime, Locktime - 1),
            Is.EqualTo(ArkadeSwapIntentStatus.Fulfilled));
    }

    [Test]
    public void SpentAtOrAfterTheDeadline_IsReportedRatherThanGuessed()
    {
        // Both our claim and the solver's reclaim are live, and telling them apart needs the
        // spending witness rather than the clock.
        Assert.That(
            ArkadeSwapIntentMonitoringService.ResolveLightningReceiveStatus(
                Vtxo(spentBy: "tx"), Locktime, Locktime),
            Is.EqualTo(ArkadeSwapIntentStatus.Resolved));
    }

    [Test]
    public void UnspentPastTheDeadline_ReportsNothing()
    {
        // The window closed and the solver will take its lockup back. Calling that Claimable would
        // invite a spend racing a reclaim for the same output.
        Assert.That(
            ArkadeSwapIntentMonitoringService.ResolveLightningReceiveStatus(Vtxo(), Locktime, Locktime + 1),
            Is.Null);
    }

    [Test]
    public void SweptLockup_IsRecoverable()
    {
        Assert.That(
            ArkadeSwapIntentMonitoringService.ResolveLightningReceiveStatus(
                Vtxo(swept: true), Locktime, Locktime - 3600),
            Is.EqualTo(ArkadeSwapIntentStatus.Recoverable));
    }

    [Test]
    public void TheTwoCorridors_ReadAnUnspentLockupOppositely()
    {
        // The distinction this whole type exists for, asserted directly.
        var funded = Vtxo();

        Assert.Multiple(() =>
        {
            Assert.That(
                ArkadeSwapIntentMonitoringService.ResolveLightningStatus(funded, Locktime, Locktime - 3600),
                Is.Null, "send: still waiting on the solver, nothing to do");
            Assert.That(
                ArkadeSwapIntentMonitoringService.ResolveLightningReceiveStatus(funded, Locktime, Locktime - 3600),
                Is.EqualTo(ArkadeSwapIntentStatus.Claimable), "receive: ours to move, and on a clock");
        });
    }

    private static ArkVtxo Vtxo(string? spentBy = null, bool swept = false) =>
        new(Script: "5120aa", TransactionId: "tx", TransactionOutputIndex: 0, Amount: 50_000,
            SpentByTransactionId: spentBy, SettledByTransactionId: null, Swept: swept,
            CreatedAt: DateTimeOffset.UtcNow, ExpiresAt: null, ExpiresAtHeight: null, ArkTxid: null);
}
