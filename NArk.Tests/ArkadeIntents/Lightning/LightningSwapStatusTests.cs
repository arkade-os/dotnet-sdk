using NArk.Abstractions.VTXOs;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Services;

namespace NArk.Tests.ArkadeIntents.Lightning;

/// <summary>
/// How a Lightning swap's lockup VTXO maps to a status.
/// </summary>
/// <remarks>
/// The asset directions can read any spend as a fill, because nobody but the solver can spend their
/// covenant. The Lightning covenant carries a refund leaf that matures on a CLTV, so the same VTXO
/// state means different things either side of that deadline — and getting it wrong would report a
/// refunded swap as a paid invoice.
/// </remarks>
[TestFixture]
public class LightningSwapStatusTests
{
    private const long Locktime = 1_800_605_184;

    [Test]
    public void SpentBeforeTheDeadline_CanOnlyBeTheSolverClaiming()
    {
        // The refund leaf is not spendable yet, so there is no other way this VTXO could have moved.
        var status = ArkadeSwapIntentMonitoringService.ResolveLightningStatus(
            Spent(), Locktime, Locktime - 1);

        Assert.That(status, Is.EqualTo(ArkadeSwapIntentStatus.Fulfilled));
    }

    [Test]
    public void SpentOnTheDeadline_IsNoLongerAttributable()
    {
        // CLTV is satisfied at equality, so from this second on a spend may be either leaf.
        var status = ArkadeSwapIntentMonitoringService.ResolveLightningStatus(
            Spent(), Locktime, Locktime);

        Assert.That(status, Is.EqualTo(ArkadeSwapIntentStatus.Resolved));
    }

    [Test]
    public void UnspentPastTheDeadline_IsRefundable()
    {
        var status = ArkadeSwapIntentMonitoringService.ResolveLightningStatus(
            Unspent(), Locktime, Locktime + 1);

        Assert.That(status, Is.EqualTo(ArkadeSwapIntentStatus.Refundable));
    }

    [Test]
    public void UnspentBeforeTheDeadline_IsNoNews()
    {
        var status = ArkadeSwapIntentMonitoringService.ResolveLightningStatus(
            Unspent(), Locktime, Locktime - 1);

        Assert.That(status, Is.Null);
    }

    [Test]
    public void ASweptLockup_IsRecoverable()
    {
        var status = ArkadeSwapIntentMonitoringService.ResolveLightningStatus(
            Vtxo(swept: true), Locktime, Locktime - 1);

        Assert.That(status, Is.EqualTo(ArkadeSwapIntentStatus.Recoverable));
    }

    [Test]
    public void TheAssetDirections_KeepReadingAnySpendAsAFill()
    {
        // Their covenant has no refund leaf, so the deadline logic must not leak into them.
        Assert.That(
            ArkadeSwapIntentMonitoringService.ResolveTerminalStatus(Spent()),
            Is.EqualTo(ArkadeSwapIntentStatus.Fulfilled));
    }

    private static ArkVtxo Spent() => Vtxo(spentBy: "b" + new string('c', 63));

    private static ArkVtxo Unspent() => Vtxo();

    private static ArkVtxo Vtxo(string? spentBy = null, bool swept = false) =>
        new(Script: "5120" + new string('a', 64), TransactionId: "tx", TransactionOutputIndex: 0,
            Amount: 50_000, SpentByTransactionId: spentBy, SettledByTransactionId: null, Swept: swept,
            CreatedAt: DateTimeOffset.UtcNow, ExpiresAt: null, ExpiresAtHeight: null);
}
