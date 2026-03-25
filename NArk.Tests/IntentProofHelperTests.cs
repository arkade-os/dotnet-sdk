using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Scripts;
using NArk.Core.Helpers;
using NBitcoin;
using NSubstitute;

namespace NArk.Tests;

[TestFixture]
public class IntentProofHelperTests
{
    private readonly Network _network = Network.RegTest;

    private ArkCoin CreateTestCoin(long satoshis = 100_000)
    {
        var key = new Key();
        var script = key.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);
        var outpoint = new OutPoint(RandomUtils.GetUInt256(), 0);
        var txOut = new TxOut(Money.Satoshis(satoshis), script);

        var scriptBuilder = Substitute.For<ScriptBuilder>();
        scriptBuilder.BuildScript().Returns(Enumerable.Empty<Op>());
        scriptBuilder.Build().Returns(new TapScript(Script.Empty, TapLeafVersion.C0));

        var contract = Substitute.For<ArkContract>(
            KeyExtensions.ParseOutputDescriptor(
                "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88",
                _network));

        return new ArkCoin(
            walletIdentifier: "test-wallet",
            contract: contract,
            birth: DateTimeOffset.UtcNow,
            expiresAt: null,
            expiresAtHeight: null,
            outPoint: outpoint,
            txOut: txOut,
            signerDescriptor: null,
            spendingScriptBuilder: scriptBuilder,
            spendingConditionWitness: null,
            lockTime: null,
            sequence: new Sequence(1),
            swept: false,
            unrolled: false);
    }

    [Test]
    public void CreateBip322Psbt_ProducesCorrectStructure()
    {
        var coin = CreateTestCoin();
        var message = "{\"type\":\"register\",\"cosigners_public_keys\":[],\"valid_at\":0,\"expire_at\":0}";

        var psbt = IntentProofHelper.CreateBip322Psbt(message, _network, coin);

        var tx = psbt.GetGlobalTransaction();

        // Should have 2 inputs: toSpend ref (index 0) + real coin (index 1)
        Assert.That(tx.Inputs.Count, Is.EqualTo(2));

        // Input[0] references the toSpend output (vout=0)
        Assert.That(tx.Inputs[0].PrevOut.N, Is.EqualTo(0u));

        // Input[1] references the real coin outpoint
        Assert.That(tx.Inputs[1].PrevOut, Is.EqualTo(coin.Outpoint));

        // Should have 1 OP_RETURN output
        Assert.That(tx.Outputs.Count, Is.EqualTo(1));
        Assert.That(tx.Outputs[0].ScriptPubKey.IsUnspendable, Is.True);

        // Transaction version should be 2
        Assert.That((int)tx.Version, Is.EqualTo(2));
    }

    [Test]
    public void CreateBip322Psbt_ToSpendOutputPaysToCoinsScript()
    {
        var coin = CreateTestCoin();
        var message = "test-message";

        var psbt = IntentProofHelper.CreateBip322Psbt(message, _network, coin);

        // The toSpend tx should be recoverable from the PSBT's non-witness UTXO
        var toSpendTxOut = psbt.Inputs[0].GetTxOut();
        Assert.That(toSpendTxOut, Is.Not.Null);

        // toSpend output pays to the coin's scriptPubKey
        Assert.That(toSpendTxOut!.ScriptPubKey, Is.EqualTo(coin.ScriptPubKey));
    }

    [Test]
    public void CreateBip322Psbt_DifferentMessages_ProduceDifferentToSpendTxIds()
    {
        var coin = CreateTestCoin();

        var psbt1 = IntentProofHelper.CreateBip322Psbt("message-1", _network, coin);
        var psbt2 = IntentProofHelper.CreateBip322Psbt("message-2", _network, coin);

        // Different messages produce different tagged hashes, hence different toSpend txids
        Assert.That(psbt1.GetGlobalTransaction().Inputs[0].PrevOut.Hash,
            Is.Not.EqualTo(psbt2.GetGlobalTransaction().Inputs[0].PrevOut.Hash));
    }

    [Test]
    public void CreateBip322Psbt_SameInputs_IsDeterministic()
    {
        var coin = CreateTestCoin();
        var message = "same-message";

        var psbt1 = IntentProofHelper.CreateBip322Psbt(message, _network, coin);
        var psbt2 = IntentProofHelper.CreateBip322Psbt(message, _network, coin);

        // Same message + same coin should produce identical PSBT structure
        Assert.That(psbt1.GetGlobalTransaction().Inputs[0].PrevOut.Hash,
            Is.EqualTo(psbt2.GetGlobalTransaction().Inputs[0].PrevOut.Hash));
    }
}
