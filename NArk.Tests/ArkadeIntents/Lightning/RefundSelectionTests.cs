using NArk.Abstractions.VTXOs;
using NArk.ArkadeIntents.Lightning;

namespace NArk.Tests.ArkadeIntents.Lightning;

/// <summary>
/// Which outputs a refund takes, and what it says when it can take none.
/// </summary>
/// <remarks>
/// The leaf a refund spends is ours alone — nobody else is coming to look at what it leaves behind.
/// So the cost of taking too few is permanent, and there is no matching risk in taking too many: a
/// refund publishes no secret and pays an address the covenant already committed to.
/// </remarks>
[TestFixture]
public class RefundSelectionTests
{
    [Test]
    public void EveryLiveOutput_IsRefunded_NotJustTheFirst()
    {
        // A retried funding leaves two outputs at one lockup. Refunding one strands the other with
        // no second path to it.
        var vtxos = new[] { Live(3_000, vout: 0), Live(1_500, vout: 1) };

        var selected = LightningSwapClient.SelectRefundable(vtxos, "swap-1");

        Assert.That(selected.Sum(v => (long)v.Amount), Is.EqualTo(4_500));
    }

    [Test]
    public void AnUnderfundedLockup_IsStillRefunded()
    {
        // Unlike a claim, there is no amount gate: whatever the solver actually put there is ours to
        // take back, and refusing because it is less than quoted would strand it.
        var vtxos = new[] { Live(7) };

        Assert.That(LightningSwapClient.SelectRefundable(vtxos, "swap-1"), Has.Count.EqualTo(1));
    }

    [Test]
    public void SpentAndSweptOutputs_AreNotSpentAgain()
    {
        var vtxos = new[] { Live(1_000, vout: 0), Spent(2_000, vout: 1), Swept(3_000, vout: 2) };

        var selected = LightningSwapClient.SelectRefundable(vtxos, "swap-1");

        Assert.That(selected.Single().Amount, Is.EqualTo(1_000UL));
    }

    [Test]
    public void ASweptLockup_IsNamedRatherThanReportedEmpty()
    {
        // "The operator swept your deposit" and "there is nothing here" are different facts, and a
        // wallet that says the second when the first is true sends the user looking in the wrong place.
        var vtxos = new[] { Swept(5_000) };

        var ex = Assert.Throws<InvalidOperationException>(
            () => LightningSwapClient.SelectRefundable(vtxos, "swap-1"));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("swept"));
            Assert.That(ex.Message, Does.Contain("bbbb"), "the swept outpoint should be named");
            Assert.That(ex.Message, Does.Not.Contain("nothing to refund"));
        });
    }

    [Test]
    public void AnEmptyLockup_SaysSo()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => LightningSwapClient.SelectRefundable([], "swap-1"));

        Assert.That(ex!.Message, Does.Contain("nothing to refund"));
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private static ArkVtxo Live(ulong sats, uint vout = 0) => Vtxo(sats, spent: false, swept: false, vout);

    private static ArkVtxo Spent(ulong sats, uint vout = 0) => Vtxo(sats, spent: true, swept: false, vout);

    private static ArkVtxo Swept(ulong sats, uint vout = 0) => Vtxo(sats, spent: false, swept: true, vout);

    private static ArkVtxo Vtxo(ulong sats, bool spent, bool swept, uint vout = 0) =>
        new(Script: "5120" + new string('a', 64), TransactionId: new string('b', 64),
            TransactionOutputIndex: vout, Amount: sats,
            SpentByTransactionId: spent ? new string('c', 64) : null, SettledByTransactionId: null,
            Swept: swept, CreatedAt: DateTimeOffset.UtcNow, ExpiresAt: null, ExpiresAtHeight: null,
            ArkTxid: null);
}
