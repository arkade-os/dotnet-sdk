using NArk.Arkade.Emulator;
using NArk.Arkade.Scripts;
using NBitcoin;

namespace NArk.Tests.Arkade;

public class NonInteractiveCoinTests
{
    [Test]
    public void Claim_SelectsCommittedLeafAndCovenantWithoutAWalletSigner()
    {
        var contract = NonInteractiveTestData.Contract();
        var vtxo = NonInteractiveTestData.Vtxos(contract, NonInteractiveTestData.Funding(contract, 50_000)).Single();
        var coin = contract.ToNonInteractiveClaimCoin("watch-only", vtxo, NonInteractiveTestData.Preimage);

        Assert.That(coin.SignerDescriptor, Is.Null);
        Assert.That(coin.SpendingScript.Script, Is.EqualTo(contract.CreateNonInteractiveClaimScript().Build().Script));
        Assert.That(coin.SpendingConditionWitness!.Pushes.Single(), Is.EqualTo(NonInteractiveTestData.Preimage));
        Assert.That(((IArkadeBoundScriptBuilder)coin.SpendingScriptBuilder).ArkadeScript,
            Is.EqualTo(contract.NonInteractiveClaimArkadeScript));
        Assert.That(ArkadePsbtExtensions.RequiresEmulatorCoSigning([coin]), Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Claim_RejectsInvalidPreimageBeforeBuildingWitness(bool wrongLength)
    {
        var contract = NonInteractiveTestData.Contract();
        var vtxo = NonInteractiveTestData.Vtxos(contract, NonInteractiveTestData.Funding(contract, 50_000)).Single();
        Assert.Throws<ArgumentException>(() => contract.ToNonInteractiveClaimCoin("watch-only", vtxo,
            wrongLength ? new byte[31] : new byte[32]));
    }

    [Test]
    public void Claim_RejectsMissingLeaf()
    {
        var contract = NonInteractiveTestData.Contract(claim: false);
        var vtxo = NonInteractiveTestData.Vtxos(contract, NonInteractiveTestData.Funding(contract, 50_000)).Single();
        Assert.Throws<InvalidOperationException>(() => contract.ToNonInteractiveClaimCoin("watch-only", vtxo,
            NonInteractiveTestData.Preimage));
    }

    [TestCase("spent")]
    [TestCase("swept")]
    [TestCase("wrong-script")]
    public void Claim_RejectsUnusableLockup(string kind)
    {
        var contract = NonInteractiveTestData.Contract();
        var vtxo = NonInteractiveTestData.Vtxos(contract, NonInteractiveTestData.Funding(contract, 50_000)).Single();
        vtxo = kind switch
        {
            "spent" => vtxo with { SpentByTransactionId = uint256.One.ToString() },
            "swept" => vtxo with { Swept = true },
            _ => vtxo with { Script = "5120" + new string('a', 64) }
        };
        Assert.Throws<InvalidOperationException>(() => contract.ToNonInteractiveClaimCoin("watch-only", vtxo,
            NonInteractiveTestData.Preimage));
    }

    [Test]
    public void Refund_SelectsNinthLeafAndLocktimeWithoutSignerOrPreimage()
    {
        var contract = NonInteractiveTestData.Contract();
        var vtxo = NonInteractiveTestData.Vtxos(contract, NonInteractiveTestData.Funding(contract, 50_000)).Single();
        var coin = contract.ToNonInteractiveRefundWithoutReceiverCoin("watch-only", vtxo);

        Assert.That(coin.SignerDescriptor, Is.Null);
        Assert.That(coin.SpendingConditionWitness, Is.Null);
        Assert.That(coin.LockTime, Is.EqualTo(contract.RefundLocktime));
        Assert.That(coin.SpendingScript.Script,
            Is.EqualTo(contract.CreateNonInteractiveRefundWithoutReceiverScript().Build().Script));
        Assert.That(((IArkadeBoundScriptBuilder)coin.SpendingScriptBuilder).ArkadeScript,
            Is.EqualTo(contract.NonInteractiveRefundArkadeScript));
        Assert.That(((IArkadeBoundScriptBuilder)coin.SpendingScriptBuilder).ArkadeScript,
            Is.EqualTo(NArk.Arkade.Contracts.VHTLCv2Contract.EnforcePayTo(contract.NonInteractiveRefund!.SenderPkScript)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Refund_RejectsContractWithoutNinthLeaf(bool refundPresent)
    {
        var contract = NonInteractiveTestData.Contract(refund: refundPresent, ninthLeaf: false);
        var vtxo = NonInteractiveTestData.Vtxos(contract, NonInteractiveTestData.Funding(contract, 50_000)).Single();
        Assert.Throws<InvalidOperationException>(() => contract.ToNonInteractiveRefundWithoutReceiverCoin("watch-only", vtxo));
    }
}
