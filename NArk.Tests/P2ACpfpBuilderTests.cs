using NArk.Core.Exit;
using NBitcoin;

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
        var feeUtxo = new OutPoint(uint256.One, 0);
        var feeUtxoPrevOut = new TxOut(Money.Satoshis(10000), feeKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86));

        Assert.Throws<InvalidOperationException>(() =>
            P2ACpfpBuilder.BuildCpfpChild(
                parent,
                new FeeRate(Money.Satoshis(5)),
                feeUtxo,
                feeUtxoPrevOut,
                new Key().GetScriptPubKey(ScriptPubKeyType.TaprootBIP86),
                feeKey));
    }

    [Test]
    public void BuildCpfpChild_CreatesV3Transaction()
    {
        // Build a parent with P2A anchor
        var parent = Network.RegTest.CreateTransaction();
        parent.Version = 3;
        parent.Outputs.Add(new TxOut(Money.Satoshis(5000), new Key().GetScriptPubKey(ScriptPubKeyType.TaprootBIP86)));
        parent.Outputs.Add(new TxOut(Money.Zero, Script.FromHex("51024e73")));

        var feeKey = new Key();
        var feeUtxo = new OutPoint(uint256.Parse("abcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcd"), 0);
        var feeUtxoPrevOut = new TxOut(Money.Satoshis(50000), feeKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86));
        var changeScript = new Key().GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);

        var child = P2ACpfpBuilder.BuildCpfpChild(
            parent,
            new FeeRate(Money.Satoshis(2)),
            feeUtxo,
            feeUtxoPrevOut,
            changeScript,
            feeKey);

        // Verify v3
        Assert.That(child.Version, Is.EqualTo(3u));

        // Verify 2 inputs: P2A anchor + fee UTXO
        Assert.That(child.Inputs.Count, Is.EqualTo(2));
        Assert.That(child.Inputs[0].PrevOut, Is.EqualTo(new OutPoint(parent, 1))); // P2A anchor
        Assert.That(child.Inputs[1].PrevOut, Is.EqualTo(feeUtxo)); // Fee UTXO

        // Verify has change output
        Assert.That(child.Outputs.Count, Is.GreaterThanOrEqualTo(1));
        Assert.That(child.Outputs[0].ScriptPubKey, Is.EqualTo(changeScript));

        // Verify fee UTXO input is signed (has witness)
        Assert.That(child.Inputs[1].WitScript, Is.Not.EqualTo(WitScript.Empty));
    }
}
