using NArk.Arkade.Contracts;
using NArk.ArkadeIntents.Covclaim;
using NBitcoin;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace NArk.Tests.ArkadeIntents.Covclaim;

/// <summary>The rule that makes a second claimant safe to add: it can fail, and the swap does not.</summary>
/// <remarks>
/// True only while the registration is best-effort: a receive that refused to proceed because the
/// daemon was down would have traded a redundant claimant for no swap at all.
/// </remarks>
[TestFixture]
public class CovclaimdRegistrationTests
{
    [Test]
    public async Task WithNoDaemonConfigured_NothingIsRegisteredAndNothingFails()
    {
        // The default deployment: this wallet claims alone, exactly as before.
        var registered = await CovclaimdRegistration.TryRegisterAsync(
            client: null, Contract(), "tark1q", new byte[32]);

        Assert.That(registered, Is.False);
    }

    [Test]
    public async Task ADaemonThatIsDown_DoesNotFailTheSwap()
    {
        var client = Substitute.For<ICovclaimdClient>();
        client.RevealAsync(default!, default!, default!, default!, default)
            .ThrowsAsyncForAnyArgs(new CovclaimdException("unreachable"));

        var registered = await CovclaimdRegistration.TryRegisterAsync(
            client, Contract(), "tark1q", new byte[32]);

        Assert.That(registered, Is.False);
    }

    [Test]
    public async Task AnUnexpectedFailure_IsNotSwallowed()
    {
        // Only the daemon's own failure mode is absorbed; a bug on this side surfaces.
        var client = Substitute.For<ICovclaimdClient>();
        client.RevealAsync(default!, default!, default!, default!, default)
            .ThrowsAsyncForAnyArgs(new InvalidOperationException("bug"));

        Assert.That(() => CovclaimdRegistration.TryRegisterAsync(client, Contract(), "tark1q", new byte[32]),
            Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public async Task ALockupWithNoClaimLeaf_IsNotRegistered()
    {
        // Without that leaf the daemon has no closure to spend, so it would refuse on arrival.
        var client = Substitute.For<ICovclaimdClient>();

        var registered = await CovclaimdRegistration.TryRegisterAsync(
            client, Contract(withClaimLeaf: false), "tark1q", new byte[32]);

        Assert.Multiple(() =>
        {
            Assert.That(registered, Is.False);
            Assert.That(client.ReceivedCalls(), Is.Empty);
        });
    }

    [Test]
    public async Task TheScriptAndTreeComeFromTheContractItself()
    {
        var client = Substitute.For<ICovclaimdClient>();
        var contract = Contract();

        await CovclaimdRegistration.TryRegisterAsync(client, contract, "tark1qaddress", new byte[32]);

        await client.Received(1).RevealAsync(
            "tark1qaddress",
            Arg.Any<byte[]>(),
            Arg.Is<byte[]>(s => s.SequenceEqual(contract.NonInteractiveClaimArkadeScript)),
            Arg.Is<TapScript[]>(t => t.Length == contract.GetTapScriptList().Length),
            Arg.Any<CancellationToken>());
    }

    private static VHTLCv2Contract Contract(bool withClaimLeaf = true) => new(
        LockupShapes.RandomDescriptor(),
        LockupShapes.RandomDescriptor(),
        LockupShapes.RandomDescriptor(),
        new uint160(System.Security.Cryptography.RandomNumberGenerator.GetBytes(20), false),
        new LockTime(1_800_600_000),
        new Sequence(TimeSpan.FromSeconds(512)),
        new Sequence(TimeSpan.FromSeconds(512)),
        new Sequence(TimeSpan.FromSeconds(1024)),
        withClaimLeaf
            ? new VHTLCv2NonInteractiveClaim(LockupShapes.RandomP2trPkScript(), LockupShapes.RandomXOnly())
            : null);
}
