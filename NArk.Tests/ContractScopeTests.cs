using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Core.Contracts;
using NArk.Core.Enums;
using NArk.Core.Extensions;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Tests;

[TestFixture]
public class ContractScopeTests
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

    private static readonly OutputDescriptor TestReceiverKey =
        KeyExtensions.ParseOutputDescriptor(
            "02a1633cafcc01ebfb6d78e39f687a1f0995c62fc95f51ead10a02ee0be551b5dc",
            Network.RegTest);

    private static readonly Sequence DefaultExitDelay = new(144);

    private static ArkBoardingContract CreateBoarding()
        => new(TestServerKey, DefaultExitDelay, TestUserKey);

    private static ArkPaymentContract CreatePayment()
        => new(TestServerKey, DefaultExitDelay, TestUserKey);

    private static HashLockedArkPaymentContract CreateHashLockPayment()
        => new(TestServerKey, DefaultExitDelay, TestUserKey,
            Convert.FromHexString("00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff"),
            HashLockTypeOption.Sha256);

    private static ArkDelegateContract CreateDelegate()
        => new(TestServerKey, DefaultExitDelay, TestUserKey, TestDelegateKey);

    private static VHTLCContract CreateVhtlc()
        => new(TestServerKey, TestUserKey, TestReceiverKey,
            Convert.FromHexString("00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff"),
            new LockTime(1000),
            new Sequence(144),
            new Sequence(144),
            new Sequence(144));

    private static ArkNoteContract CreateNote()
        => new(50_000, Convert.FromHexString("00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff"));

    private static UnknownArkContract CreateUnknown()
        => new(
            ArkAddress.Parse(
                "tark1qz4d2t2czchfaml2l3ad3gwde2qxpd0srhc7wkpnvtg99cnxyz8c3pnvvhnhumhwhqthmlxmdryakwx99s6508y8dunj9sty2p5mr7unh5re63"),
            TestServerKey,
            mainnet: false);

    [Test]
    public void BoardingContract_DefaultScope_IsOnchain()
    {
        Assert.That(CreateBoarding().DefaultScope, Is.EqualTo(ContractScope.Onchain));
    }

    [Test]
    public void PaymentContract_DefaultScope_IsOffchain()
    {
        Assert.That(CreatePayment().DefaultScope, Is.EqualTo(ContractScope.Offchain));
    }

    [Test]
    public void HashLockPaymentContract_DefaultScope_IsOffchain()
    {
        Assert.That(CreateHashLockPayment().DefaultScope, Is.EqualTo(ContractScope.Offchain));
    }

    [Test]
    public void DelegateContract_DefaultScope_IsOffchain()
    {
        Assert.That(CreateDelegate().DefaultScope, Is.EqualTo(ContractScope.Offchain));
    }

    [Test]
    public void VhtlcContract_DefaultScope_IsOffchain()
    {
        Assert.That(CreateVhtlc().DefaultScope, Is.EqualTo(ContractScope.Offchain));
    }

    [Test]
    public void NoteContract_DefaultScope_IsOffchain()
    {
        Assert.That(CreateNote().DefaultScope, Is.EqualTo(ContractScope.Offchain));
    }

    [Test]
    public void UnknownContract_DefaultScope_IsOffchain()
    {
        Assert.That(CreateUnknown().DefaultScope, Is.EqualTo(ContractScope.Offchain));
    }

    [Test]
    public void ToEntity_NullOverride_UsesDefaultScope()
    {
        var boarding = CreateBoarding().ToEntity("w");
        Assert.That(boarding.Scope, Is.EqualTo(ContractScope.Onchain));

        var payment = CreatePayment().ToEntity("w");
        Assert.That(payment.Scope, Is.EqualTo(ContractScope.Offchain));
    }

    [Test]
    public void ToEntity_ExplicitOverride_UsesOverride()
    {
        // Boarding defaults Onchain; override to Offchain should win.
        var overridden = CreateBoarding().ToEntity("w", scopeOverride: ContractScope.Offchain);
        Assert.That(overridden.Scope, Is.EqualTo(ContractScope.Offchain));

        // Payment defaults Offchain; override to Both should win.
        var both = CreatePayment().ToEntity("w", scopeOverride: ContractScope.Onchain | ContractScope.Offchain);
        Assert.That(both.Scope, Is.EqualTo(ContractScope.Onchain | ContractScope.Offchain));
    }

    [Test]
    public void Flags_Both_IsOnchainOrOffchain()
    {
        var both = ContractScope.Onchain | ContractScope.Offchain;

        // A "Both" value satisfies both an Onchain-include and an Offchain-include
        // bitwise check — the same predicate EF Core translates to SQL.
        Assert.That((both & ContractScope.Onchain) == ContractScope.Onchain, Is.True);
        Assert.That((both & ContractScope.Offchain) == ContractScope.Offchain, Is.True);

        // A pure Onchain value satisfies the Onchain check but not the Offchain check.
        Assert.That((ContractScope.Onchain & ContractScope.Onchain) == ContractScope.Onchain, Is.True);
        Assert.That((ContractScope.Onchain & ContractScope.Offchain) == ContractScope.Offchain, Is.False);
    }
}
