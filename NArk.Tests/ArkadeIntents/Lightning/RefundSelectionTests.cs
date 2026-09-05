using NArk.Abstractions.VTXOs;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Recovery;

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

        var selected = LightningIntentsClient.SelectRefundable(vtxos, "swap-1");

        Assert.That(selected.Sum(v => (long)v.Amount), Is.EqualTo(4_500));
    }

    [Test]
    public void AnUnderfundedLockup_IsStillRefunded()
    {
        // Unlike a claim, there is no amount gate: whatever the solver actually put there is ours to
        // take back, and refusing because it is less than quoted would strand it.
        var vtxos = new[] { Live(7) };

        Assert.That(LightningIntentsClient.SelectRefundable(vtxos, "swap-1"), Has.Count.EqualTo(1));
    }

    [Test]
    public void AlreadySpentOutputs_AreNotSpentAgain()
    {
        var vtxos = new[] { Live(1_000, vout: 0), Spent(2_000, vout: 1) };

        var selected = LightningIntentsClient.SelectRefundable(vtxos, "swap-1");

        Assert.That(selected.Single().Amount, Is.EqualTo(1_000UL));
    }

    [Test]
    public void ASweptSibling_StopsThePushRatherThanBeingLeftBehind()
    {
        // Previously this refunded the live output and said nothing about the swept one. That is the
        // worse failure of the two available: refunding the remainder reports success over money
        // that never moved, and a caller who believes the swap is refunded stops watching the part
        // still sitting there. Refusing keeps the whole amount visible as a thing to deal with.
        var vtxos = new[] { Live(1_000, vout: 0), Swept(3_000, vout: 2) };

        var ex = Assert.Throws<LockupNeedsRecoveryException>(
            () => LightningIntentsClient.SelectRefundable(vtxos, "swap-1"));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Fate, Is.EqualTo(LockupFate.Swept));
            Assert.That(ex.Outpoints.Single().N, Is.EqualTo(2));
        });
    }

    [Test]
    public void AnExitedSibling_StopsThePushToo()
    {
        // An unrolled output sits on-chain under the same script, where this off-chain leaf cannot
        // reach it. It is unspent and unswept, so it used to be selected and the spend would simply
        // fail — after the rest had already been refunded.
        var vtxos = new[] { Live(1_000, vout: 0), Exited(4_000, vout: 1) };

        var ex = Assert.Throws<LockupNeedsRecoveryException>(
            () => LightningIntentsClient.SelectRefundable(vtxos, "swap-1"));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Fate, Is.EqualTo(LockupFate.Exited));
            Assert.That(ex.Outpoints.Single().N, Is.EqualTo(1));
        });
    }

    [Test]
    public void ARecoveryRefusal_IsStillAnInvalidOperation()
    {
        // The advance loop catches InvalidOperationException to keep one swap from ending a pass
        // over many. A recovery exception outside that hierarchy would escape the sweep entirely,
        // turning a swap that merely needs attention into a crash that stops every other one.
        Assert.That(
            new LockupNeedsRecoveryException(LockupFate.Swept, [], "x"),
            Is.InstanceOf<InvalidOperationException>());
        Assert.That(
            new RefundNotLocallyPossibleException(RefundBlockedReason.NoSigner, "x"),
            Is.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public void ASweptLockup_IsNamedRatherThanReportedEmpty()
    {
        // "The operator swept your deposit" and "there is nothing here" are different facts, and a
        // wallet that says the second when the first is true sends the user looking in the wrong place.
        var vtxos = new[] { Swept(5_000) };

        var ex = Assert.Throws<LockupNeedsRecoveryException>(
            () => LightningIntentsClient.SelectRefundable(vtxos, "swap-1"));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("swept"));
            Assert.That(ex.Message, Does.Contain("bbbb"), "the swept outpoint should be named");
            Assert.That(ex.Message, Does.Not.Contain("nothing to refund"));
            Assert.That(ex.Fate, Is.EqualTo(LockupFate.Swept));
            Assert.That(ex.Outpoints, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void AnEmptyLockup_SaysSo()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => LightningIntentsClient.SelectRefundable([], "swap-1"));

        Assert.That(ex!.Message, Does.Contain("nothing to refund"));
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private static ArkVtxo Live(ulong sats, uint vout = 0) => Vtxo(sats, spent: false, swept: false, vout);

    private static ArkVtxo Spent(ulong sats, uint vout = 0) => Vtxo(sats, spent: true, swept: false, vout);

    private static ArkVtxo Swept(ulong sats, uint vout = 0) => Vtxo(sats, spent: false, swept: true, vout);

    private static ArkVtxo Exited(ulong sats, uint vout = 0) =>
        Vtxo(sats, spent: false, swept: false, vout) with { Unrolled = true };

    private static ArkVtxo Vtxo(ulong sats, bool spent, bool swept, uint vout = 0) =>
        new(Script: "5120" + new string('a', 64), TransactionId: new string('b', 64),
            TransactionOutputIndex: vout, Amount: sats,
            SpentByTransactionId: spent ? new string('c', 64) : null, SettledByTransactionId: null,
            Swept: swept, CreatedAt: DateTimeOffset.UtcNow, ExpiresAt: null, ExpiresAtHeight: null,
            ArkTxid: null);
}
