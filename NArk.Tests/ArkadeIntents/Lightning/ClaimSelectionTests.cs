using NArk.Abstractions.VTXOs;
using NArk.ArkadeIntents.Lightning;

namespace NArk.Tests.ArkadeIntents.Lightning;

/// <summary>
/// Which outputs at a lockup a claim may spend, and how much of them.
/// </summary>
[TestFixture]
public class ClaimSelectionTests
{
    private const ulong Quoted = 50_000;

    [Test]
    public void TheExactFunding_IsSelected()
    {
        var funding = Vtxo(amount: Quoted);

        var selected = LightningReceiveClient.SelectClaimable([funding], Quoted, "swap-1");

        Assert.That(selected, Is.EqualTo(new[] { funding }));
    }

    [Test]
    public void ASplitFunding_IsClaimedTogether()
    {
        // A solver funding in two outputs is still the funding. Claiming only one could leave the
        // outpoint the solver watches unspent — settled, with the preimage never seen.
        var first = Vtxo(vout: 0, amount: Quoted - 10_000);
        var second = Vtxo(vout: 1, amount: 10_000);

        var selected = LightningReceiveClient.SelectClaimable([first, second], Quoted, "swap-1");

        Assert.That(selected, Is.EquivalentTo(new[] { first, second }));
    }

    [Test]
    public void LessThanTheQuote_KeepsThePreimageSecret()
    {
        // Claiming publishes the preimage — the secret that settles the payer's invoice. Handing it
        // over for less than the swap promised is paying the solver for money it never delivered.
        var underfunded = Vtxo(amount: Quoted - 1);

        var ex = Assert.Throws<InvalidOperationException>(
            () => LightningReceiveClient.SelectClaimable([underfunded], Quoted, "swap-1"));

        Assert.That(ex!.Message, Does.Contain("refusing to publish the preimage"));
    }

    [Test]
    public void NothingFunded_SaysSo()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => LightningReceiveClient.SelectClaimable([], Quoted, "swap-1"));

        Assert.That(ex!.Message, Does.Contain("has not funded"));
    }

    [Test]
    public void SpentAndSweptOutputs_DoNotCountTowardTheCover()
    {
        var spent = Vtxo(vout: 0, amount: Quoted, spentBy: new string('c', 64));
        var swept = Vtxo(vout: 1, amount: Quoted, swept: true);

        Assert.Throws<InvalidOperationException>(
            () => LightningReceiveClient.SelectClaimable([spent, swept], Quoted, "swap-1"));
    }

    private static ArkVtxo Vtxo(uint vout = 0, ulong amount = Quoted, string? spentBy = null, bool swept = false) =>
        new(Script: "5120" + new string('a', 64), TransactionId: new string('b', 64),
            TransactionOutputIndex: vout, Amount: amount,
            SpentByTransactionId: spentBy, SettledByTransactionId: null, Swept: swept,
            CreatedAt: DateTimeOffset.UtcNow, ExpiresAt: null, ExpiresAtHeight: null, ArkTxid: null);
}
