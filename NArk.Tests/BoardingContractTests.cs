using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Core.Contracts;
using NArk.Core.Extensions;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Tests;

[TestFixture]
public class BoardingContractTests
{
    private static readonly OutputDescriptor TestServerKey =
        KeyExtensions.ParseOutputDescriptor(
            "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88",
            Network.RegTest);

    private static readonly OutputDescriptor TestUserKey =
        KeyExtensions.ParseOutputDescriptor(
            "030192e796452d6df9697c280542e1560557bcf79a347d925895043136225c7cb4",
            Network.RegTest);

    private static readonly OutputDescriptor DifferentUserKey =
        KeyExtensions.ParseOutputDescriptor(
            "02a1633cafcc01ebfb6d78e39f687a1f0995c62fc95f51ead10a02ee0be551b5dc",
            Network.RegTest);

    private static readonly Sequence DefaultExitDelay = new(144);

    [Test]
    public void BoardingContract_GeneratesCorrectTapscriptTree()
    {
        var contract = new ArkBoardingContract(TestServerKey, DefaultExitDelay, TestUserKey);

        var leaves = contract.GetTapScriptList();

        Assert.That(leaves, Has.Length.EqualTo(2));

        // Collaborative path: server CHECKSIGVERIFY + user CHECKSIG
        var collabScript = leaves[0].Script;
        Assert.That(collabScript.ToString(), Does.Contain("OP_CHECKSIGVERIFY"));
        Assert.That(collabScript.ToString(), Does.Contain("OP_CHECKSIG"));

        // Unilateral path: CSV timelock
        var exitScript = leaves[1].Script;
        Assert.That(exitScript.ToString(), Does.Contain("OP_CSV"));
    }

    [Test]
    public void BoardingContract_ProducesDeterministicAddress()
    {
        var contract1 = new ArkBoardingContract(TestServerKey, DefaultExitDelay, TestUserKey);
        var contract2 = new ArkBoardingContract(TestServerKey, DefaultExitDelay, TestUserKey);

        var address1 = contract1.GetArkAddress();
        var address2 = contract2.GetArkAddress();

        Assert.That(address1.ScriptPubKey.ToHex(), Is.EqualTo(address2.ScriptPubKey.ToHex()));
    }

    [Test]
    public void BoardingContract_DifferentKeys_DifferentAddresses()
    {
        var contract1 = new ArkBoardingContract(TestServerKey, DefaultExitDelay, TestUserKey);
        var contract2 = new ArkBoardingContract(TestServerKey, DefaultExitDelay, DifferentUserKey);

        var address1 = contract1.GetArkAddress();
        var address2 = contract2.GetArkAddress();

        Assert.That(address1.ScriptPubKey.ToHex(), Is.Not.EqualTo(address2.ScriptPubKey.ToHex()));
    }

    [Test]
    public void BoardingContract_ParseRoundTrip()
    {
        var original = new ArkBoardingContract(TestServerKey, DefaultExitDelay, TestUserKey);
        var entity = original.ToEntity("test-wallet");

        // Parse back from entity contract data
        var parsed = ArkBoardingContract.Parse(entity.AdditionalData, Network.RegTest);

        var originalAddress = original.GetArkAddress();
        var parsedAddress = parsed.GetArkAddress();

        Assert.That(parsedAddress.ScriptPubKey.ToHex(), Is.EqualTo(originalAddress.ScriptPubKey.ToHex()));
    }

    [Test]
    public void BoardingContract_TypeIsBoarding()
    {
        var contract = new ArkBoardingContract(TestServerKey, DefaultExitDelay, TestUserKey);

        Assert.That(contract.Type, Is.EqualTo("Boarding"));
        Assert.That(ArkBoardingContract.ContractType, Is.EqualTo("Boarding"));
    }
}
