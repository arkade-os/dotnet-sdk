using NArk.ArkadeIntents.Onchain;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Tests.ArkadeIntents.Onchain;

/// <summary>
/// Reading an L1 HTLC's state back off the chain, for a client that no longer knows it.
/// </summary>
/// <remarks>
/// The drive path never needs this — it moves forward from a row it wrote itself. This is the
/// recovery path: a restored wallet, a process down across the window, an operator asking what
/// actually happened. The two ask different questions ("may I act" versus "what is true"), and the
/// answers here have to be derived from the chain rather than from anybody's account of it.
/// </remarks>
[TestFixture]
public class OnchainHtlcStateTests
{
    private static readonly byte[] Preimage = Enumerable.Repeat((byte)0xa3, 32).ToArray();
    private static ECPrivKey ClaimKey => ECPrivKey.Create(Enumerable.Repeat((byte)0x11, 32).ToArray());
    private static ECPrivKey RefundKey => ECPrivKey.Create(Enumerable.Repeat((byte)0x22, 32).ToArray());

    private static uint256 PaymentHash =>
        new(System.Security.Cryptography.SHA256.HashData(Preimage), lendian: false);

    [Test]
    public void APreimageIsRecoveredFromTheSpendingWitness()
    {
        // The L1 counterpart of SwapPreimageReader, which reads Arkade spends through the indexer
        // and so cannot answer for a Bitcoin transaction at all.
        var tx = Transaction.Create(Network.RegTest);
        tx.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0))
        {
            WitScript = new WitScript(
                Op.GetPushOp(new byte[64]),
                Op.GetPushOp(Preimage),
                Op.GetPushOp(new byte[] { 0x51 }),
                Op.GetPushOp(new byte[33])),
        });

        Assert.That(OnchainHtlcState.ExtractPreimage(tx, PaymentHash), Is.EqualTo(Preimage));
    }

    [Test]
    public void AThirtyTwoBytePushThatIsNotThePreimage_IsNotBelieved()
    {
        // A push of the right shape is not evidence. One that hashes to this swap's payment hash is,
        // and that is evidence nobody can forge — which is what makes acting on it safe without
        // trusting whoever built the spend.
        var tx = Transaction.Create(Network.RegTest);
        tx.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0))
        {
            WitScript = new WitScript(Op.GetPushOp(Enumerable.Repeat((byte)0x5c, 32).ToArray())),
        });

        Assert.That(OnchainHtlcState.ExtractPreimage(tx, PaymentHash), Is.Null);
    }

    [Test]
    public void ASpendWithNoWitnessAtAll_AnswersNull()
    {
        // Null never means "refunded" on its own: a transaction that carried no preimage and one we
        // could not read are the same silence, and both mean only "not provably a claim".
        var tx = Transaction.Create(Network.RegTest);
        tx.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));

        Assert.That(OnchainHtlcState.ExtractPreimage(tx, PaymentHash), Is.Null);
    }

    [Test]
    public void ThePreimageIsFoundOnAnyInput_NotOnlyTheFirst()
    {
        // A sweep of several HTLC outputs signs each input separately; which one a caller happens to
        // look at first is not a fact about the swap.
        var tx = Transaction.Create(Network.RegTest);
        tx.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0))
        {
            WitScript = new WitScript(Op.GetPushOp(new byte[64])),
        });
        tx.Inputs.Add(new TxIn(new OutPoint(uint256.One, 1))
        {
            WitScript = new WitScript(Op.GetPushOp(new byte[64]), Op.GetPushOp(Preimage)),
        });

        Assert.That(OnchainHtlcState.ExtractPreimage(tx, PaymentHash), Is.EqualTo(Preimage));
    }

    [Test]
    public void TheRefundablePhaseMeansTheClaimWindowIsClosed_NotThatAClaimIsStillAvailable()
    {
        // Pinned as a statement about the vocabulary rather than about a code path: a recovery
        // caller that reads `Refundable` and tries to claim has misread the phase, and the enum's
        // own doc comment is the only thing standing between that reading and a lost claim.
        var htlc = OnchainHtlc.Derive(
            PaymentHash, ClaimKey.CreateXOnlyPubKey(), RefundKey.CreateXOnlyPubKey(),
            1_800_000_000, Network.RegTest);

        Assert.Multiple(() =>
        {
            // Due exactly at the leaf's own locktime, measured on the chain's clock.
            Assert.That(OnchainReceiveGates.RefundIsDue(htlc.RefundLocktime.Value, 1_800_000_000), Is.True);
            Assert.That(OnchainReceiveGates.RefundIsDue(htlc.RefundLocktime.Value, 1_799_999_999), Is.False);
        });
    }
}
