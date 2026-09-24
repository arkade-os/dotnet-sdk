using NArk.Abstractions.Blockchain;
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

    private static readonly TimeHeight Now = new(DateTimeOffset.UnixEpoch.AddDays(1), 100);

    [Test]
    public void TheExactFunding_IsSelected()
    {
        var funding = Vtxo(amount: Quoted);

        var selected = LightningIntentsClient.SelectClaimable([funding], Quoted, "swap-1", Now);

        Assert.That(selected, Is.EqualTo(new[] { funding }));
    }

    [Test]
    public void ASplitFunding_IsClaimedTogether()
    {
        // A solver funding in two outputs is still the funding. Claiming only one could leave the
        // outpoint the solver watches unspent — settled, with the preimage never seen.
        var first = Vtxo(vout: 0, amount: Quoted - 10_000);
        var second = Vtxo(vout: 1, amount: 10_000);

        var selected = LightningIntentsClient.SelectClaimable([first, second], Quoted, "swap-1", Now);

        Assert.That(selected, Is.EquivalentTo(new[] { first, second }));
    }

    [Test]
    public void LessThanTheQuote_KeepsThePreimageSecret()
    {
        // Claiming publishes the preimage — the secret that settles the payer's invoice. Handing it
        // over for less than the swap promised is paying the solver for money it never delivered.
        var underfunded = Vtxo(amount: Quoted - 1);

        var ex = Assert.Throws<InvalidOperationException>(
            () => LightningIntentsClient.SelectClaimable([underfunded], Quoted, "swap-1", Now));

        Assert.That(ex!.Message, Does.Contain("refusing to publish the preimage"));
    }

    [Test]
    public void NothingFunded_SaysSo()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => LightningIntentsClient.SelectClaimable([], Quoted, "swap-1", Now));

        Assert.That(ex!.Message, Does.Contain("has not funded"));
    }

    [Test]
    public void SpentAndSweptOutputs_DoNotCountTowardTheCover()
    {
        var spent = Vtxo(vout: 0, amount: Quoted, spentBy: new string('c', 64));
        var swept = Vtxo(vout: 1, amount: Quoted, swept: true);

        Assert.Throws<InvalidOperationException>(
            () => LightningIntentsClient.SelectClaimable([spent, swept], Quoted, "swap-1", Now));
    }

    // An expired lockup cannot be spent offchain even before the server sweeps it, so a claim built
    // from one is refused here rather than by arkd, and the reason names the lapse.
    [Test]
    public void AnExpiredLockup_IsRefusedAndSaidSo()
    {
        var lapsed = Vtxo(amount: Quoted, expiresAt: Now.Timestamp.AddMinutes(-1));

        var ex = Assert.Throws<InvalidOperationException>(
            () => LightningIntentsClient.SelectClaimable([lapsed], Quoted, "swap-1", Now));

        Assert.That(ex!.Message, Does.Contain("past the batch"));
        Assert.That(ex.Message, Does.Not.Contain("has not funded"));
    }

    [Test]
    public void ALockupPastItsExpiryHeight_IsRefusedToo()
    {
        var lapsed = Vtxo(amount: Quoted, expiresAtHeight: Now.Height);

        Assert.Throws<InvalidOperationException>(
            () => LightningIntentsClient.SelectClaimable([lapsed], Quoted, "swap-1", Now));
    }

    // A local clock runs ahead of median time past, so standing in for the chain's would refuse claims
    // the chain still accepts. On a receive that is the delivery, so an unknown clock judges nothing.
    [Test]
    public void WithNoChainClock_ExpiryIsNotJudgedAtAll()
    {
        var lapsed = Vtxo(amount: Quoted, expiresAt: Now.Timestamp.AddMinutes(-1));

        Assert.That(
            LightningIntentsClient.SelectClaimable([lapsed], Quoted, "swap-1", null),
            Is.EqualTo(new[] { lapsed }));
    }

    [Test]
    public void ALockupStillInsideItsBatch_IsClaimed()
    {
        var live = Vtxo(amount: Quoted, expiresAt: Now.Timestamp.AddMinutes(1));

        Assert.That(
            LightningIntentsClient.SelectClaimable([live], Quoted, "swap-1", Now),
            Is.EqualTo(new[] { live }));
    }

    private static ArkVtxo Vtxo(
        uint vout = 0, ulong amount = Quoted, string? spentBy = null, bool swept = false,
        DateTimeOffset? expiresAt = null, uint? expiresAtHeight = null) =>
        new(Script: "5120" + new string('a', 64), TransactionId: new string('b', 64),
            TransactionOutputIndex: vout, Amount: amount,
            SpentByTransactionId: spentBy, SettledByTransactionId: null, Swept: swept,
            CreatedAt: DateTimeOffset.UnixEpoch, ExpiresAt: expiresAt, ExpiresAtHeight: expiresAtHeight,
            ArkTxid: null);
}
