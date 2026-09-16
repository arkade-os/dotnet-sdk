using NArk.Abstractions.Blockchain;
using NArk.ArkadeIntents.Onchain;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Tests.ArkadeIntents.Onchain;

/// <summary>
/// The transaction that takes an L1 HTLC back once its refund leaf has matured.
/// </summary>
/// <remarks>
/// The on-board's only recourse: on that leg nothing of ours was ever funded on Arkade, so if the
/// solver never delivers, this leaf is the whole way home. Two of its properties are consensus rules
/// rather than conventions and both fail silently — an <c>nLockTime</c> below the leaf's value fails
/// the script, and a final input sequence turns <c>OP_CHECKLOCKTIMEVERIFY</c> into a no-op and gets
/// the transaction rejected as non-final instead. Neither shows up as a malformed transaction.
/// </remarks>
[TestFixture]
public class OnchainRefundBuilderTests
{
    private static readonly byte[] Preimage = Enumerable.Repeat((byte)0xa3, 32).ToArray();
    private static ECPrivKey ClaimKey => ECPrivKey.Create(Enumerable.Repeat((byte)0x11, 32).ToArray());
    private static ECPrivKey RefundKey => ECPrivKey.Create(Enumerable.Repeat((byte)0x22, 32).ToArray());
    private const long Locktime = 1_800_000_000;

    private static OnchainHtlc Htlc() => OnchainHtlc.Derive(
        new uint256(System.Security.Cryptography.SHA256.HashData(Preimage), lendian: false),
        ClaimKey.CreateXOnlyPubKey(),
        RefundKey.CreateXOnlyPubKey(),
        Locktime,
        Network.RegTest);

    private static BoardingUtxo Utxo(ulong amount = 100_000) => new(
        Txid: new string('7', 64), Vout: 0, Amount: amount,
        Confirmed: true, BlockHeight: 100, BlockTime: Locktime - 3600);

    private static BitcoinAddress Refund =>
        BitcoinAddress.Create("bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", Network.RegTest);

    private static Task<SecpSchnorrSignature> SignWithRefundKey(uint256 hash) =>
        Task.FromResult(RefundKey.SignBIP340(hash.ToBytes(false)));

    [Test]
    public async Task TheWitnessTakesTheRefundLeaf_AndItsSignatureVerifiesAgainstTheSighash()
    {
        var htlc = Htlc();
        var utxo = Utxo();
        var tx = await OnchainRefundBuilder.BuildAsync(
            htlc, [utxo], Refund, new FeeRate(Money.Satoshis(2), 1), SignWithRefundKey);

        var witness = tx.Inputs[0].WitScript;
        var spent = new[] { new TxOut(Money.Satoshis(utxo.Amount), htlc.PkScript) };
        var leaf = htlc.RefundLeaf.ToTapScript(TapLeafVersion.C0);
        var execData = new TaprootExecutionData(0, leaf.LeafHash) { SigHash = TaprootSigHash.Default };
        var sighash = tx.GetSignatureHashTaproot(spent, execData);

        Assert.Multiple(() =>
        {
            // Three items, not the claim leaf's four: this leaf takes no preimage.
            Assert.That(witness.PushCount, Is.EqualTo(3));

            var signature = SecpSchnorrSignature.TryCreate(witness[0], out var parsed)
                ? parsed! : throw new AssertionException("witness[0] is not a BIP-340 signature");
            Assert.That(
                RefundKey.CreateXOnlyPubKey().SigVerifyBIP340(signature, sighash.ToBytes(false)),
                Is.True, "the signature does not verify against the sighash recomputed here");

            Assert.That(witness[1], Is.EqualTo(htlc.RefundLeaf.ToBytes()));
            Assert.That(witness[2], Is.EqualTo(htlc.RefundControlBlock.ToBytes()));
        });
    }

    [Test]
    public async Task TheTransactionCarriesTheLeafsLocktime()
    {
        // CLTV compares the stack value against nLockTime, so anything lower fails the script — on a
        // transaction that is otherwise perfectly formed.
        var tx = await OnchainRefundBuilder.BuildAsync(
            Htlc(), [Utxo()], Refund, new FeeRate(Money.Satoshis(2), 1), SignWithRefundKey);

        Assert.That((uint)tx.LockTime, Is.EqualTo((uint)Locktime));
    }

    [Test]
    public async Task EveryInputIsNonFinal_OrTheTimelockIsNotEnforcedAtAll()
    {
        // A sequence of 0xFFFFFFFF makes OP_CHECKLOCKTIMEVERIFY a no-op: the script would pass with
        // any locktime, and the node would then reject the transaction as non-final. Two failures
        // that look nothing like "the sequence was wrong".
        var tx = await OnchainRefundBuilder.BuildAsync(
            Htlc(), [Utxo(), Utxo() with { Vout = 1 }], Refund,
            new FeeRate(Money.Satoshis(1), 1), SignWithRefundKey);

        Assert.Multiple(() =>
        {
            foreach (var input in tx.Inputs)
            {
                Assert.That((uint)input.Sequence, Is.Not.EqualTo(0xFFFFFFFFu));
                Assert.That((uint)input.Sequence, Is.EqualTo(OnchainRefundBuilder.TimelockedSequence));
            }
        });
    }

    [Test]
    public async Task TheEstimatedSizeMatchesWhatWasActuallyBuilt()
    {
        // Priced before the signatures exist, so a drift means the refund either overpays or sticks
        // — and sticking is the case where the sats do not come home at all.
        var htlc = Htlc();
        var tx = await OnchainRefundBuilder.BuildAsync(
            htlc, [Utxo(), Utxo() with { Vout = 1 }], Refund,
            new FeeRate(Money.Satoshis(1), 1), SignWithRefundKey);

        var estimated = OnchainRefundBuilder.VirtualSize(htlc, inputCount: 2, Refund);

        Assert.That(tx.GetVirtualSize(), Is.EqualTo(estimated).Within(2),
            $"estimated {estimated} vbytes, built {tx.GetVirtualSize()}");
    }

    [Test]
    public async Task EveryOutputAtTheHtlc_IsSwept()
    {
        // Refunding one would leave the rest behind a leaf only the counterparty can now take.
        var tx = await OnchainRefundBuilder.BuildAsync(
            Htlc(), [Utxo(), Utxo() with { Vout = 1 }, Utxo() with { Vout = 2 }], Refund,
            new FeeRate(Money.Satoshis(1), 1), SignWithRefundKey);

        Assert.That(tx.Inputs, Has.Count.EqualTo(3));
    }

    [Test]
    public void APayoutUnderTheDustLimit_IsRefused()
    {
        Assert.ThrowsAsync<InvalidOperationException>(() => OnchainRefundBuilder.BuildAsync(
            Htlc(), [Utxo(amount: 600)], Refund,
            new FeeRate(Money.Satoshis(20), 1), SignWithRefundKey));
    }

    [Test]
    public void NothingAtTheHtlc_IsRefused()
    {
        Assert.ThrowsAsync<InvalidOperationException>(() => OnchainRefundBuilder.BuildAsync(
            Htlc(), [], Refund, new FeeRate(Money.Satoshis(2), 1), SignWithRefundKey));
    }
}
