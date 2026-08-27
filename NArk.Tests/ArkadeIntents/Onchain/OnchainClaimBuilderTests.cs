using System.Security.Cryptography;
using NArk.Abstractions.Blockchain;
using NArk.ArkadeIntents.Onchain;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Tests.ArkadeIntents.Onchain;

/// <summary>
/// The transaction that publishes a preimage to take an HTLC on Bitcoin L1.
/// </summary>
/// <remarks>
/// The only code in this corridor that signs on Bitcoin, and the shape of a script-path spend is
/// forgiving in the worst way: a wrong sighash, a wrong leaf hash or a reversed witness all produce
/// a well-formed transaction the network rejects, after the swap has been reported as claimed. So
/// the signature is checked against a sighash recomputed here rather than taken from the builder.
/// </remarks>
[TestFixture]
public class OnchainClaimBuilderTests
{
    private static readonly byte[] Preimage = Enumerable.Repeat((byte)0xa3, 32).ToArray();
    private static ECPrivKey ClaimKey => ECPrivKey.Create(Enumerable.Repeat((byte)0x11, 32).ToArray());
    private static ECPrivKey RefundKey => ECPrivKey.Create(Enumerable.Repeat((byte)0x22, 32).ToArray());
    private const long Locktime = 1_800_000_000;

    private static OnchainHtlc Htlc() => OnchainHtlc.Derive(
        new uint256(System.Security.Cryptography.SHA256.HashData(Preimage)),
        ClaimKey.CreateXOnlyPubKey(),
        RefundKey.CreateXOnlyPubKey(),
        Locktime,
        Network.RegTest);

    private static BoardingUtxo Utxo(ulong amount = 100_000) => new(
        Txid: new string('7', 64), Vout: 0, Amount: amount,
        Confirmed: true, BlockHeight: 100, BlockTime: Locktime - 3600);

    private static BitcoinAddress Payout =>
        BitcoinAddress.Create("bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", Network.RegTest);

    private static Task<SecpSchnorrSignature> SignWithClaimKey(uint256 hash) =>
        Task.FromResult(ClaimKey.SignBIP340(hash.ToBytes(false)));

    [Test]
    public async Task TheWitnessRevealsThePreimage_AndItsSignatureVerifiesAgainstTheSighash()
    {
        var htlc = Htlc();
        var utxo = Utxo();
        var tx = await OnchainClaimBuilder.BuildAsync(
            htlc, [utxo], Preimage, Payout, new FeeRate(Money.Satoshis(2), 1), SignWithClaimKey);

        var witness = tx.Inputs[0].WitScript;
        var spent = new[] { new TxOut(Money.Satoshis(utxo.Amount), htlc.PkScript) };
        var leaf = htlc.ClaimLeaf.ToTapScript(TapLeafVersion.C0);
        var execData = new TaprootExecutionData(0, leaf.LeafHash) { SigHash = TaprootSigHash.Default };
        var sighash = tx.GetSignatureHashTaproot(spent, execData);

        Assert.Multiple(() =>
        {
            // Four items: the two the leaf consumes, then the script and its control block.
            Assert.That(witness.PushCount, Is.EqualTo(4));

            var signature = SecpSchnorrSignature.TryCreate(witness[0], out var parsed)
                ? parsed! : throw new AssertionException("witness[0] is not a BIP-340 signature");
            Assert.That(
                ClaimKey.CreateXOnlyPubKey().SigVerifyBIP340(signature, sighash.ToBytes(false)),
                Is.True, "the signature does not verify against the sighash recomputed here");

            Assert.That(witness[1], Is.EqualTo(Preimage), "the preimage is not where the leaf reads it");
            Assert.That(witness[2], Is.EqualTo(htlc.ClaimLeaf.ToBytes()));
            Assert.That(witness[3], Is.EqualTo(htlc.ClaimControlBlock.ToBytes()));
        });
    }

    [Test]
    public async Task TheFeeComesOutOfTheSweep_AndLeavesMostOfIt()
    {
        var utxo = Utxo();
        var tx = await OnchainClaimBuilder.BuildAsync(
            Htlc(), [utxo], Preimage, Payout, new FeeRate(Money.Satoshis(2), 1), SignWithClaimKey);

        Assert.Multiple(() =>
        {
            Assert.That(tx.Outputs, Has.Count.EqualTo(1));
            Assert.That(tx.Outputs[0].Value.Satoshi, Is.LessThan((long)utxo.Amount));
            Assert.That(tx.Outputs[0].Value.Satoshi, Is.GreaterThan((long)utxo.Amount - 1000));
        });
    }

    [Test]
    public async Task TheEstimatedSizeMatchesWhatWasActuallyBuilt()
    {
        // The fee has to be priced before the signatures exist, so the estimate is computed from the
        // parts. If it drifts from the real thing the claim is either overpaying or stuck.
        var htlc = Htlc();
        var tx = await OnchainClaimBuilder.BuildAsync(
            htlc, [Utxo(), Utxo() with { Vout = 1 }], Preimage, Payout,
            new FeeRate(Money.Satoshis(1), 1), SignWithClaimKey);

        var estimated = OnchainClaimBuilder.VirtualSize(htlc, inputCount: 2, Payout);

        Assert.That(tx.GetVirtualSize(), Is.EqualTo(estimated).Within(2),
            $"estimated {estimated} vbytes, built {tx.GetVirtualSize()}");
    }

    [Test]
    public async Task EveryOutputAtTheHtlc_IsSwept()
    {
        // Claiming one would leave the rest for the counterparty's refund leaf.
        var tx = await OnchainClaimBuilder.BuildAsync(
            Htlc(), [Utxo(), Utxo() with { Vout = 1 }, Utxo() with { Vout = 2 }],
            Preimage, Payout, new FeeRate(Money.Satoshis(1), 1), SignWithClaimKey);

        Assert.That(tx.Inputs, Has.Count.EqualTo(3));
    }

    [Test]
    public void APreimageThatIsNotThisHtlcs_IsRefused()
    {
        var wrong = RandomNumberGenerator.GetBytes(32);

        Assert.ThrowsAsync<ArgumentException>(() => OnchainClaimBuilder.BuildAsync(
            Htlc(), [Utxo()], wrong, Payout, new FeeRate(Money.Satoshis(2), 1), SignWithClaimKey));
    }

    [Test]
    public void APayoutUnderTheDustLimit_IsRefused()
    {
        // Better to leave it and let the refund path take it than to broadcast something no node
        // relays while reporting the swap as claimed.
        Assert.ThrowsAsync<InvalidOperationException>(() => OnchainClaimBuilder.BuildAsync(
            Htlc(), [Utxo(amount: 600)], Preimage, Payout,
            new FeeRate(Money.Satoshis(20), 1), SignWithClaimKey));
    }

    [Test]
    public void NothingAtTheHtlc_IsRefused()
    {
        Assert.ThrowsAsync<InvalidOperationException>(() => OnchainClaimBuilder.BuildAsync(
            Htlc(), [], Preimage, Payout, new FeeRate(Money.Satoshis(2), 1), SignWithClaimKey));
    }
}
