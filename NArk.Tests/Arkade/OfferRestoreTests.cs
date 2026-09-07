using NArk.Abstractions.Extensions;
using NArk.Abstractions.Helpers;
using NArk.ArkadeIntents.Assets;
using NArk.Core.Assets;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Tests.Arkade;

/// <summary>
/// Rebuilding an asset swap from the chain, and reading what became of its deposit.
/// </summary>
/// <remarks>
/// The swap store dies with its storage backend, and none of what it held is only there: the funding
/// transaction carries the offer, the covenant holds the deposit, and the spender says what happened
/// to it. These pin the two readings that make the rest recomputable.
/// </remarks>
[TestFixture]
public class OfferRestoreTests
{
    private static readonly OutputDescriptor Server = KeyExtensions.ParseOutputDescriptor(
        "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88", Network.RegTest);

    private static readonly OutputDescriptor OtherServer = KeyExtensions.ParseOutputDescriptor(
        "02" + Convert.ToHexString(XOnly(31)).ToLowerInvariant(), Network.RegTest);

    // ─── Reading the offer back out of a funding transaction ──────────

    [Test]
    public void AnOfferIsReadBackOutOfTheFundingTransaction()
    {
        var offer = SampleOffer();
        var tx = FundingTx(offer);

        var recovered = OfferRestore.OfferIn(tx);

        Assert.That(recovered, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(recovered!.SwapPkScript, Is.EqualTo(offer.SwapPkScript));
            Assert.That(recovered.WantAmount, Is.EqualTo(offer.WantAmount));
            Assert.That(recovered.MakerPublicKey, Is.EqualTo(offer.MakerPublicKey));
        });
    }

    [Test]
    public void ATransactionCarryingNoExtension_IsNotAnOffer()
    {
        // Most of a wallet's history is not an offer funding, and a scan over it is the normal
        // caller — so this answers rather than throws.
        var tx = Transaction.Create(Network.RegTest);
        tx.Outputs.Add(Money.Satoshis(1000), new Script(TaprootSpk(1)));

        Assert.That(OfferRestore.OfferIn(tx), Is.Null);
    }

    [Test]
    public void AnExtensionWithoutAnOfferPacket_IsNotAnOffer()
    {
        var tx = Transaction.Create(Network.RegTest);
        tx.Outputs.Add(new Extension([new UnknownPacket(0x7f, [1, 2, 3])]).ToTxOut());

        Assert.That(OfferRestore.OfferIn(tx), Is.Null);
    }

    // ─── Classifying the spend by the leaf it took ────────────────────

    [Test]
    public void AFulfillLeafMeansTheSolverPaidForIt()
    {
        var offer = SampleOffer();
        var spend = SpendTakingLeaf(offer, "fulfill", out var deposit);

        Assert.That(
            OfferRestore.ClassifySpend(offer, Server, Network.RegTest, spend, deposit),
            Is.EqualTo(OfferSpendKind.Fulfilled));
    }

    [Test]
    public void ACancelLeafMeansTheDepositWentBack()
    {
        var offer = SampleOffer();
        var spend = SpendTakingLeaf(offer, "cancel", out var deposit);

        Assert.That(
            OfferRestore.ClassifySpend(offer, Server, Network.RegTest, spend, deposit),
            Is.EqualTo(OfferSpendKind.Cancelled));
    }

    [Test]
    public void AnExitLeafAlsoMeansTheDepositWentBack()
    {
        // `cancel` and `exit` differ in who had to agree — cooperatively with the signer, or the
        // maker alone after a delay — and not in what became of the deposit, which is the question.
        // Reporting `exit` as indeterminate would be worse than imprecise: no status gets written,
        // so the swap stays pending and every later scan re-queues it forever.
        var offer = SampleOffer(withExit: true);
        var spend = SpendTakingLeaf(offer, "exit", out var deposit);

        Assert.That(
            OfferRestore.ClassifySpend(offer, Server, Network.RegTest, spend, deposit),
            Is.EqualTo(OfferSpendKind.Cancelled));
    }

    [Test]
    public void ARotatedServerKey_IsNotGuessedAgainst()
    {
        // Rebuilt against a different server key the covenant is a different tree, so its leaves say
        // nothing about this deposit. Answering anything but indeterminate here would be confidently
        // describing somebody else's script.
        var offer = SampleOffer();
        var spend = SpendTakingLeaf(offer, "fulfill", out var deposit);

        Assert.That(
            OfferRestore.ClassifySpend(offer, OtherServer, Network.RegTest, spend, deposit),
            Is.EqualTo(OfferSpendKind.Indeterminate));
    }

    [Test]
    public void ASpendOfSomeOtherOutpoint_IsNotThisDepositsFate()
    {
        // A solver filling several offers in one transaction gives each input its own leaf, so the
        // outpoint is what ties a leaf to this swap.
        var offer = SampleOffer();
        var spend = SpendTakingLeaf(offer, "fulfill", out var deposit);
        var elsewhere = new OutPoint(deposit.Hash, deposit.N + 1);

        Assert.That(
            OfferRestore.ClassifySpend(offer, Server, Network.RegTest, spend, elsewhere),
            Is.EqualTo(OfferSpendKind.Indeterminate));
    }

    [Test]
    public void ASpendCarryingNoTapleaf_IsIndeterminate()
    {
        // A batch forfeit, or the wrong half of the spend. Both are "no answer", and recording a
        // guess is how a returned deposit becomes a settled sale.
        var offer = SampleOffer();
        var deposit = new OutPoint(uint256.One, 0);
        var tx = Transaction.Create(Network.RegTest);
        tx.Inputs.Add(new TxIn(deposit));
        var psbt = PSBT.FromTransaction(tx, Network.RegTest);

        Assert.That(
            OfferRestore.ClassifySpend(offer, Server, Network.RegTest, psbt, deposit),
            Is.EqualTo(OfferSpendKind.Indeterminate));
    }

    // ─── Fixtures ─────────────────────────────────────────────────────

    private static Offer SampleOffer(bool withExit = false)
    {
        var created = OfferBuilder.CreateOffer(
            TaprootSpk(2), XOnly(3), XOnly(4), Server, Network.RegTest,
            wantAmount: 500,
            wantAsset: AssetId.Create(Convert.ToHexString(XOnly(9)), 7),
            exitDelay: withExit ? new Sequence(TimeSpan.FromSeconds(4096)) : null);

        return OfferCodec.Decode(created.Payload);
    }

    private static Transaction FundingTx(Offer offer)
    {
        var tx = Transaction.Create(Network.RegTest);
        tx.Outputs.Add(Money.Satoshis(10_000), new Script(offer.SwapPkScript));
        tx.Outputs.Add(new Extension([OfferPacket.FromOffer(offer)]).ToTxOut());
        return tx;
    }

    /// <summary>A spend of the deposit that takes the named covenant leaf.</summary>
    private static PSBT SpendTakingLeaf(Offer offer, string function, out OutPoint deposit)
    {
        var contract = OfferBuilder.BuildContract(offer, Server, Network.RegTest);
        var leaf = contract.FunctionByName(function)
            ?? throw new InvalidOperationException($"the covenant has no '{function}' leaf");

        deposit = new OutPoint(uint256.One, 0);

        var tx = Transaction.Create(Network.RegTest);
        tx.Inputs.Add(new TxIn(deposit));
        tx.Outputs.Add(Money.Satoshis(9_000), new Script(TaprootSpk(2)));

        var psbt = PSBT.FromTransaction(tx, Network.RegTest);
        psbt.Inputs[0].SetTaprootLeafScript(
            contract.GetTaprootSpendInfo(), new Script(leaf.LeafScript).ToTapScript(TapLeafVersion.C0));
        return psbt;
    }

    private static Key KeyFor(byte seed) => new(Enumerable.Repeat(seed, 32).ToArray());

    private static byte[] XOnly(byte seed) => KeyFor(seed).PubKey.TaprootInternalKey.ToBytes();

    private static byte[] TaprootSpk(byte seed) => [0x51, 0x20, .. XOnly(seed)];
}
