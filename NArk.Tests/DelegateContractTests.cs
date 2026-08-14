using NArk.Abstractions.Extensions;
using NArk.Core.Contracts;
using NArk.Core.Extensions;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Tests;

[TestFixture]
public class DelegateContractTests
{
    private static readonly OutputDescriptor TestServerKey =
        KeyExtensions.ParseOutputDescriptor(
            "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88",
            Network.RegTest);

    private static readonly OutputDescriptor TestUserKey =
        KeyExtensions.ParseOutputDescriptor(
            "030192e796452d6df9697c280542e1560557bcf79a347d925895043136225c7cb4",
            Network.RegTest);

    private static readonly OutputDescriptor TestDelegateKey =
        KeyExtensions.ParseOutputDescriptor(
            "021e1bb85455fe3f5aed60d101aa4dbdb9e7714f6226769a97a17a5331dadcd53b",
            Network.RegTest);

    private static readonly OutputDescriptor DifferentDelegateKey =
        KeyExtensions.ParseOutputDescriptor(
            "02a1633cafcc01ebfb6d78e39f687a1f0995c62fc95f51ead10a02ee0be551b5dc",
            Network.RegTest);

    private static readonly Sequence DefaultExitDelay = new(144);
    private static readonly LockTime DefaultCltvLocktime = new(1000);

    private ArkDelegateContract CreateContract(
        OutputDescriptor? delegateKey = null,
        LockTime? cltvLocktime = null,
        bool useCltv = true)
    {
        return new ArkDelegateContract(
            TestServerKey,
            DefaultExitDelay,
            TestUserKey,
            delegateKey ?? TestDelegateKey,
            useCltv ? (cltvLocktime ?? DefaultCltvLocktime) : null);
    }

    [Test]
    public void DelegateContract_GeneratesThreeTapLeaves()
    {
        var contract = CreateContract();
        var leaves = contract.GetTapScriptList();
        Assert.That(leaves, Has.Length.EqualTo(3));
    }

    // Leaf ordering: [0] forfeit, [1] exit, [2] delegate

    [Test]
    public void DelegateContract_DelegatePath_WithCLTV_HasCltvGate()
    {
        var contract = CreateContract();
        var delegateScript = contract.GetTapScriptList()[2].Script;

        Assert.That(delegateScript.ToString(), Does.Contain("OP_CLTV"));
        Assert.That(delegateScript.ToString(), Does.Contain("OP_CHECKSIGVERIFY"));
    }

    [Test]
    public void DelegateContract_DelegatePath_WithoutCLTV_NoCltvGate()
    {
        var contract = CreateContract(useCltv: false);
        var delegateScript = contract.GetTapScriptList()[2].Script;

        Assert.That(delegateScript.ToString(), Does.Not.Contain("OP_CLTV"));
        // Still has multisig: user + delegate CHECKSIGVERIFY, server CHECKSIG
        Assert.That(delegateScript.ToString(), Does.Contain("OP_CHECKSIGVERIFY"));
        Assert.That(delegateScript.ToString(), Does.Contain("OP_CHECKSIG"));
    }

    [Test]
    public void DelegateContract_CollaborativePath_HasChecksigVerify()
    {
        var contract = CreateContract();
        var forfeitScript = contract.GetTapScriptList()[0].Script;

        Assert.That(forfeitScript.ToString(), Does.Contain("OP_CHECKSIGVERIFY"));
        Assert.That(forfeitScript.ToString(), Does.Contain("OP_CHECKSIG"));
    }

    [Test]
    public void DelegateContract_ExitPath_HasCSV()
    {
        var contract = CreateContract();
        var exitScript = contract.GetTapScriptList()[1].Script;

        Assert.That(exitScript.ToString(), Does.Contain("OP_CSV"));
    }

    [Test]
    public void DelegateContract_ProducesDeterministicAddress()
    {
        var contract1 = CreateContract();
        var contract2 = CreateContract();

        Assert.That(contract1.GetScriptPubKey().ToHex(), Is.EqualTo(contract2.GetScriptPubKey().ToHex()));
    }

    [Test]
    public void DelegateContract_DifferentKeys_DifferentAddresses()
    {
        var contract1 = CreateContract(delegateKey: TestDelegateKey);
        var contract2 = CreateContract(delegateKey: DifferentDelegateKey);

        Assert.That(contract1.GetScriptPubKey().ToHex(), Is.Not.EqualTo(contract2.GetScriptPubKey().ToHex()));
    }

    [Test]
    public void DelegateContract_DifferentCltv_DifferentAddresses()
    {
        var contract1 = CreateContract(cltvLocktime: new LockTime(1000));
        var contract2 = CreateContract(cltvLocktime: new LockTime(2000));

        Assert.That(contract1.GetScriptPubKey().ToHex(), Is.Not.EqualTo(contract2.GetScriptPubKey().ToHex()));
    }

    [Test]
    public void DelegateContract_WithAndWithoutCltv_DifferentAddresses()
    {
        var withCltv = CreateContract(useCltv: true);
        var withoutCltv = CreateContract(useCltv: false);

        Assert.That(withCltv.GetScriptPubKey().ToHex(), Is.Not.EqualTo(withoutCltv.GetScriptPubKey().ToHex()));
    }

    [Test]
    public void DelegateContract_ParseRoundTrip_WithCltv()
    {
        var original = CreateContract();
        var entity = original.ToEntity("test-wallet");

        var parsed = (ArkDelegateContract)ArkDelegateContract.Parse(entity.AdditionalData, Network.RegTest);

        Assert.That(parsed.GetScriptPubKey().ToHex(), Is.EqualTo(original.GetScriptPubKey().ToHex()));
        Assert.That(parsed.CltvLocktime, Is.EqualTo(original.CltvLocktime));
    }

    [Test]
    public void DelegateContract_ParseRoundTrip_WithoutCltv()
    {
        var original = CreateContract(useCltv: false);
        var entity = original.ToEntity("test-wallet");

        var parsed = (ArkDelegateContract)ArkDelegateContract.Parse(entity.AdditionalData, Network.RegTest);

        Assert.That(parsed.GetScriptPubKey().ToHex(), Is.EqualTo(original.GetScriptPubKey().ToHex()));
        Assert.That(parsed.CltvLocktime, Is.Null);
    }

    [Test]
    public void DelegateContract_ParseViaContractParser()
    {
        var original = CreateContract();
        var entity = original.ToEntity("test-wallet");

        var parsed = ArkContractParser.Parse(entity.Type, entity.AdditionalData, Network.RegTest);

        Assert.That(parsed, Is.InstanceOf<ArkDelegateContract>());
        Assert.That(parsed!.GetScriptPubKey().ToHex(), Is.EqualTo(original.GetScriptPubKey().ToHex()));
    }

    [Test]
    public void DelegateContract_GetArkAddress_ReturnsValidAddress()
    {
        var contract = CreateContract();
        var arkAddress = contract.GetArkAddress();

        Assert.That(arkAddress, Is.Not.Null);
        Assert.That(arkAddress.ToString(false), Does.StartWith("tark1q"));
    }

    [Test]
    public void DelegateContract_TypeIsDelegate()
    {
        var contract = CreateContract();

        Assert.That(contract.Type, Is.EqualTo("Delegate"));
        Assert.That(ArkDelegateContract.ContractType, Is.EqualTo("Delegate"));
    }
}
