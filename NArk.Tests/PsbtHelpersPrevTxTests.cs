using System.Text;
using NArk.Abstractions.Helpers;
using NBitcoin;

namespace NArk.Tests;

/// <summary>
/// Covers the <c>prevarktx</c> / <c>prevouttx</c> ark PSBT fields (key type
/// <c>0xde</c>) that carry the previous transaction an introspection opcode
/// reads. Matches <c>arkade-os/emulator pkg/arkade/psbt_fields.go</c>
/// (<c>ArkFieldPrevArkTx</c> / <c>ArkFieldPrevoutTx</c>).
/// </summary>
[TestFixture]
public class PsbtHelpersPrevTxTests
{
    [Test]
    public void PrevoutTx_RoundTrips_UnderArkFieldKey()
    {
        var prev = SampleTx(Money.Coins(1));
        var input = SingleInputPsbt().Inputs[0];

        input.SetArkFieldPrevoutTx(prev);

        // Key must be exactly [0xde] || "prevouttx".
        var expectedKey = new byte[] { 0xde }.Concat(Encoding.UTF8.GetBytes("prevouttx")).ToArray();
        Assert.That(input.Unknown.Keys.Any(k => k.SequenceEqual(expectedKey)), Is.True,
            "prevouttx field must be stored under [0xde]||\"prevouttx\"");

        var recovered = input.GetArkFieldPrevoutTx(Network.RegTest);
        Assert.That(recovered, Is.Not.Null);
        Assert.That(recovered!.GetHash(), Is.EqualTo(prev.GetHash()));
    }

    [Test]
    public void PrevArkTx_RoundTrips_UnderArkFieldKey()
    {
        var prev = SampleTx(Money.Coins(2));
        var input = SingleInputPsbt().Inputs[0];

        input.SetArkFieldPrevArkTx(prev);

        var expectedKey = new byte[] { 0xde }.Concat(Encoding.UTF8.GetBytes("prevarktx")).ToArray();
        Assert.That(input.Unknown.Keys.Any(k => k.SequenceEqual(expectedKey)), Is.True);

        var recovered = input.GetArkFieldPrevArkTx(Network.RegTest);
        Assert.That(recovered!.GetHash(), Is.EqualTo(prev.GetHash()));
    }

    [Test]
    public void GetPrevoutTx_AbsentField_ReturnsNull()
    {
        Assert.That(SingleInputPsbt().Inputs[0].GetArkFieldPrevoutTx(Network.RegTest), Is.Null);
    }

    private static Transaction SampleTx(Money amount)
    {
        var tx = Transaction.Create(Network.RegTest);
        // A dummy input so the tx serialises (NBitcoin rejects input-less txs);
        // the amount keeps each sample tx's id distinct.
        tx.Inputs.Add(new OutPoint(uint256.One, 0));
        tx.Outputs.Add(amount, new Key().GetScriptPubKey(ScriptPubKeyType.TaprootBIP86));
        return tx;
    }

    private static PSBT SingleInputPsbt()
    {
        var funding = SampleTx(Money.Coins(5));
        var spending = Transaction.Create(Network.RegTest);
        spending.Inputs.Add(new OutPoint(funding, 0));
        spending.Outputs.Add(Money.Coins(4), new Key().GetScriptPubKey(ScriptPubKeyType.TaprootBIP86));
        return PSBT.FromTransaction(spending, Network.RegTest);
    }
}
