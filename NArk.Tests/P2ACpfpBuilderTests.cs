using NArk.Abstractions.Blockchain;
using NArk.Core.Exit;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Tests;

[TestFixture]
public class P2ACpfpBuilderTests
{
    [Test]
    public void FindP2AAnchor_DetectsBip431P2A()
    {
        var tx = Network.RegTest.CreateTransaction();
        tx.Outputs.Add(new TxOut(Money.Satoshis(1000), new Key().GetScriptPubKey(ScriptPubKeyType.TaprootBIP86)));
        tx.Outputs.Add(new TxOut(Money.Satoshis(240), new Script(OpcodeType.OP_1))); // BIP 431 P2A

        var result = P2ACpfpBuilder.FindP2AAnchor(tx);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value.Outpoint.N, Is.EqualTo(1));
        Assert.That(result.Value.TxOut.Value, Is.EqualTo(Money.Satoshis(240)));
    }

    [Test]
    public void FindP2AAnchor_DetectsArkProtocolMarker()
    {
        var tx = Network.RegTest.CreateTransaction();
        tx.Outputs.Add(new TxOut(Money.Satoshis(1000), new Key().GetScriptPubKey(ScriptPubKeyType.TaprootBIP86)));
        tx.Outputs.Add(new TxOut(Money.Zero, Script.FromHex("51024e73"))); // Ark P2A

        var result = P2ACpfpBuilder.FindP2AAnchor(tx);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value.Outpoint.N, Is.EqualTo(1));
    }

    [Test]
    public void FindP2AAnchor_ReturnsNull_WhenNoAnchor()
    {
        var tx = Network.RegTest.CreateTransaction();
        tx.Outputs.Add(new TxOut(Money.Satoshis(1000), new Key().GetScriptPubKey(ScriptPubKeyType.TaprootBIP86)));

        var result = P2ACpfpBuilder.FindP2AAnchor(tx);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void BuildCpfpChild_ThrowsWhenNoAnchor()
    {
        var parent = Network.RegTest.CreateTransaction();
        parent.Outputs.Add(new TxOut(Money.Satoshis(1000), new Key().GetScriptPubKey(ScriptPubKeyType.TaprootBIP86)));

        var feeKey = new Key();
        var feeUtxo = new FeeUtxo(
            new OutPoint(uint256.One, 0),
            new TxOut(Money.Satoshis(10000), feeKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86)));

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await P2ACpfpBuilder.BuildCpfpChildAsync(
                parent,
                new FeeRate(Money.Satoshis(5)),
                feeUtxo,
                new Key().GetScriptPubKey(ScriptPubKeyType.TaprootBIP86),
                new InMemoryKeyFeeWallet(feeKey, feeUtxo)));
    }

    [Test]
    public async Task BuildCpfpChild_CreatesV3Transaction()
    {
        // Build a parent with P2A anchor
        var parent = Network.RegTest.CreateTransaction();
        parent.Version = 3;
        parent.Outputs.Add(new TxOut(Money.Satoshis(5000), new Key().GetScriptPubKey(ScriptPubKeyType.TaprootBIP86)));
        parent.Outputs.Add(new TxOut(Money.Zero, Script.FromHex("51024e73")));

        var feeKey = new Key();
        var feeUtxo = new FeeUtxo(
            new OutPoint(uint256.Parse("abcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcd"), 0),
            new TxOut(Money.Satoshis(50000), feeKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86)));
        var changeScript = new Key().GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);

        var child = await P2ACpfpBuilder.BuildCpfpChildAsync(
            parent,
            new FeeRate(Money.Satoshis(2)),
            feeUtxo,
            changeScript,
            new InMemoryKeyFeeWallet(feeKey, feeUtxo));

        // Verify v3
        Assert.That(child.Version, Is.EqualTo(3u));

        // Verify 2 inputs: P2A anchor + fee UTXO
        Assert.That(child.Inputs.Count, Is.EqualTo(2));
        Assert.That(child.Inputs[0].PrevOut, Is.EqualTo(new OutPoint(parent, 1))); // P2A anchor
        Assert.That(child.Inputs[1].PrevOut, Is.EqualTo(feeUtxo.Outpoint)); // Fee UTXO

        // Verify has change output
        Assert.That(child.Outputs.Count, Is.GreaterThanOrEqualTo(1));
        Assert.That(child.Outputs[0].ScriptPubKey, Is.EqualTo(changeScript));

        // Verify fee UTXO input is signed (has witness)
        Assert.That(child.Inputs[1].WitScript, Is.Not.EqualTo(WitScript.Empty));
    }

    [Test]
    public void BuildCpfpChild_RejectsForeignOutpoint()
    {
        // The wallet must validate the outpoint belongs to it before signing.
        // Tests the contract that IFeeWallet.SignFeeUtxoAsync isn't a key-spend
        // oracle for arbitrary outpoints.
        var parent = Network.RegTest.CreateTransaction();
        parent.Version = 3;
        parent.Outputs.Add(new TxOut(Money.Zero, Script.FromHex("51024e73")));

        var feeKey = new Key();
        var ownedUtxo = new FeeUtxo(
            new OutPoint(uint256.Parse("11" + new string('1', 62)), 0),
            new TxOut(Money.Satoshis(50000), feeKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86)));
        var foreignUtxo = new FeeUtxo(
            new OutPoint(uint256.Parse("22" + new string('2', 62)), 0),
            new TxOut(Money.Satoshis(50000), feeKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86)));

        var wallet = new InMemoryKeyFeeWallet(feeKey, ownedUtxo);

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await P2ACpfpBuilder.BuildCpfpChildAsync(
                parent,
                new FeeRate(Money.Satoshis(2)),
                foreignUtxo,
                new Key().GetScriptPubKey(ScriptPubKeyType.TaprootBIP86),
                wallet));
    }

    /// <summary>
    /// Minimal in-process IFeeWallet for unit tests: holds a single UTXO + key,
    /// validates the outpoint on signing requests, and signs via P2TR keypath.
    /// Mirrors the contract production wallets must honour while keeping the
    /// test wiring trivial.
    /// </summary>
    private sealed class InMemoryKeyFeeWallet(Key key, FeeUtxo utxo) : IFeeWallet
    {
        public Task<FeeUtxo?> SelectFeeUtxoAsync(Money minAmount, CancellationToken cancellationToken = default)
            => Task.FromResult<FeeUtxo?>(utxo.TxOut.Value >= minAmount ? utxo : null);

        public Task<SecpSchnorrSignature> SignFeeUtxoAsync(
            OutPoint feeOutpoint,
            uint256 sighash,
            TaprootSigHash sighashType,
            CancellationToken cancellationToken = default)
        {
            if (feeOutpoint != utxo.Outpoint)
                throw new InvalidOperationException(
                    $"InMemoryKeyFeeWallet: asked to sign for outpoint {feeOutpoint} which isn't owned by this wallet");

            var taprootSig = key.SignTaprootKeySpend(sighash, sighashType);
            if (!SecpSchnorrSignature.TryCreate(taprootSig.SchnorrSignature.ToBytes(), out var parsed))
                throw new InvalidOperationException("Failed to re-parse signature bytes");
            return Task.FromResult(parsed!);
        }

        public Task<Script> GetChangeScriptAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(key.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86));
    }
}
